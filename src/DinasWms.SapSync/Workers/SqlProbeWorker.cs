using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Diagnóstico de la conexión SQL y de la resolución de <c>DocEntry</c>: corre
/// una sola vez, reporta, y detiene el host.
/// </summary>
/// <remarks>
/// Verifica los tres desenlaces con documentos reales de la base, no solo el
/// camino feliz: factura abierta, cerrada y anulada. Se invoca con
/// <c>--RunMode=SqlProbe --Probe:CardCode=… --Probe:DocNum=…</c>.
/// </remarks>
public sealed class SqlProbeWorker : BackgroundService
{
    private readonly ISapSqlConnectionFactory _connectionFactory;
    private readonly IDocEntryResolver _resolver;
    private readonly SqlOptions _sqlOptions;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SqlProbeWorker> _logger;

    public SqlProbeWorker(
        ISapSqlConnectionFactory connectionFactory,
        IDocEntryResolver resolver,
        IOptions<SqlOptions> sqlOptions,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<SqlProbeWorker> logger)
    {
        _connectionFactory = connectionFactory;
        _resolver = resolver;
        _sqlOptions = sqlOptions.Value;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            await CorrerProbeAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado normal.
        }
        catch (AmbiguousInvoiceException ex)
        {
            Environment.ExitCode = 1;
            _logger.LogError("DOCUMENTO AMBIGUO. {Message}", ex.Message);
        }
        catch (SapSqlException ex)
        {
            Environment.ExitCode = 1;

            if (ex.IsAccessProblem)
            {
                _logger.LogError(
                    "PROBLEMA DE ACCESO EN SQL (error {Numero}). Esto se resuelve con un GRANT " +
                    "del lado de SQL Server, no en el código.\n{Message}",
                    ex.SqlErrorNumber,
                    ex.Message);
            }
            else
            {
                _logger.LogError("FALLO DE SQL (error {Numero}). {Message}", ex.SqlErrorNumber, ex.Message);
            }
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            _logger.LogError(ex, "PROBE DE SQL FALLIDO por un error inesperado.");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task CorrerProbeAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "=== Probe de SQL / resolución de DocEntry ===\n" +
            "  Servidor: {Server}\n" +
            "  Base:     {Database}\n" +
            "  Usuario:  {UserName}\n" +
            "  Vista:    {Vista}",
            _sqlOptions.Server,
            _sqlOptions.Database,
            _sqlOptions.UserName,
            DocEntryResolver.ViewName);

        await using var connection = await _connectionFactory
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Paso 1 OK — conexión y autenticación establecidas.");

        await ReportarPermisosAsync(connection, cancellationToken).ConfigureAwait(false);
        await ResolverCasoPedidoAsync(cancellationToken).ConfigureAwait(false);
        await VerificarLosTresDesenlacesAsync(connection, cancellationToken).ConfigureAwait(false);
        await ResponderPreguntasAbiertasAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Confirma acceso a la vista y que las tablas de SAP siguen cerradas — esa
    /// restricción es la red de seguridad del proyecto, no un estorbo.
    /// </summary>
    private async Task ReportarPermisosAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        foreach (var objeto in new[] { DocEntryResolver.ViewName, "dbo.OINV", "dbo.ORIN" })
        {
            await using var cmd = new SqlCommand(
                "SELECT HAS_PERMS_BY_NAME(@objeto, 'OBJECT', 'SELECT')", connection);
            cmd.CommandTimeout = _sqlOptions.CommandTimeoutSeconds;
            cmd.Parameters.Add("@objeto", System.Data.SqlDbType.NVarChar, 256).Value = objeto;

            var resultado = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            var estado = resultado is null || resultado == DBNull.Value
                ? "NULL (no existe o no es visible para este login)"
                : Convert.ToInt32(resultado) == 1 ? "SÍ" : "NO";

            _logger.LogInformation("Paso 2 — SELECT sobre {Objeto}: {Estado}", objeto, estado);
        }
    }

    private async Task ResolverCasoPedidoAsync(CancellationToken cancellationToken)
    {
        var cardCode = _configuration["Probe:CardCode"];
        var docNumTexto = _configuration["Probe:DocNum"];

        if (string.IsNullOrWhiteSpace(cardCode) || string.IsNullOrWhiteSpace(docNumTexto))
        {
            _logger.LogWarning(
                "Paso 3 omitido: faltan Probe:CardCode y Probe:DocNum. " +
                "Ejemplo: --Probe:CardCode=C100012 --Probe:DocNum=6152");
            return;
        }

        if (!int.TryParse(docNumTexto, out var docNum))
        {
            Environment.ExitCode = 1;
            _logger.LogError("Probe:DocNum='{Valor}' no es un entero.", docNumTexto);
            return;
        }

        var resultado = await _resolver
            .LookupInvoiceAsync(cardCode, docNum, cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "=== RESULTADO: client_code={CardCode}, doc_num={DocNum} → {Desenlace}, " +
            "DocEntry={DocEntry}, series={Series}, doc_status={Status}, anulada={Anulada}, " +
            "aplicable={Aplicable} ===",
            resultado.CardCode,
            resultado.DocNum,
            resultado.Outcome,
            resultado.DocEntry,
            resultado.Series,
            resultado.DocStatus,
            resultado.IsCanceled,
            resultado.CanApplyPayment);

        if (resultado.Outcome != InvoiceLookupOutcome.Resolved)
        {
            Environment.ExitCode = 1;
        }
    }

    /// <summary>
    /// Toma un documento real de cada estado y comprueba que el resolver lo
    /// clasifique como corresponde. Un solo caso feliz no prueba la lógica.
    /// </summary>
    private async Task VerificarLosTresDesenlacesAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        var casos = new (string Descripcion, string Filtro, InvoiceLookupOutcome Esperado)[]
        {
            ("abierta y vigente", "doc_status = 'O' AND is_canceled = 'N'", InvoiceLookupOutcome.Resolved),
            ("cerrada",           "doc_status <> 'O' AND is_canceled = 'N'", InvoiceLookupOutcome.Closed),
            ("anulada",           "is_canceled = 'Y'",                       InvoiceLookupOutcome.Canceled),
        };

        foreach (var (descripcion, filtro, esperado) in casos)
        {
            var muestra = await BuscarMuestraAsync(connection, filtro, cancellationToken)
                .ConfigureAwait(false);

            if (muestra is null)
            {
                _logger.LogWarning(
                    "Paso 4 — no hay ninguna factura {Descripcion} en esta base; ese desenlace " +
                    "({Esperado}) queda sin verificar con datos reales.",
                    descripcion,
                    esperado);
                continue;
            }

            var (cardCode, docNum, docEntryEsperado) = muestra.Value;

            var resultado = await _resolver
                .LookupInvoiceAsync(cardCode, docNum, cancellationToken)
                .ConfigureAwait(false);

            var ok = resultado.Outcome == esperado && resultado.DocEntry == docEntryEsperado;

            if (ok)
            {
                _logger.LogInformation(
                    "Paso 4 OK — factura {Descripcion}: client_code={CardCode}, doc_num={DocNum} → " +
                    "{Desenlace}, DocEntry={DocEntry}.",
                    descripcion,
                    cardCode,
                    docNum,
                    resultado.Outcome,
                    resultado.DocEntry);
            }
            else
            {
                Environment.ExitCode = 1;
                _logger.LogError(
                    "Paso 4 FALLÓ — factura {Descripcion}: client_code={CardCode}, doc_num={DocNum}. " +
                    "Se esperaba {Esperado}/DocEntry={DocEntryEsperado} y se obtuvo " +
                    "{Obtenido}/DocEntry={DocEntryObtenido}.",
                    descripcion,
                    cardCode,
                    docNum,
                    esperado,
                    docEntryEsperado,
                    resultado.Outcome,
                    resultado.DocEntry);
            }
        }
    }

    /// <summary>
    /// Las tres preguntas que quedaron abiertas cuando solo se veían las facturas
    /// abiertas. Con la vista sin filtro de estado ya se pueden responder.
    /// </summary>
    private async Task ResponderPreguntasAbiertasAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        // (1) ¿DocEntry y DocNum difieren alguna vez? Define si la resolución se
        //     puede demostrar empíricamente o no.
        var difieren = await EscalarIntAsync(
            connection,
            $"SELECT COUNT(*) FROM {DocEntryResolver.ViewName} WHERE doc_entry <> doc_num",
            cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Paso 5 — facturas donde doc_entry <> doc_num: {Cuantas}.", difieren);

        if (difieren > 0)
        {
            var muestra = await BuscarMuestraAsync(
                connection, "doc_entry <> doc_num", cancellationToken).ConfigureAwait(false);

            if (muestra is not null)
            {
                var (cardCode, docNum, docEntryEsperado) = muestra.Value;
                var resultado = await _resolver
                    .LookupInvoiceAsync(cardCode, docNum, cancellationToken)
                    .ConfigureAwait(false);

                if (resultado.DocEntry == docEntryEsperado)
                {
                    _logger.LogInformation(
                        "Paso 5 OK — caso donde difieren: client_code={CardCode}, doc_num={DocNum} → " +
                        "DocEntry={DocEntry}. DEMOSTRADO que se devuelve el DocEntry, no el DocNum.",
                        cardCode,
                        docNum,
                        resultado.DocEntry);
                }
                else
                {
                    Environment.ExitCode = 1;
                    _logger.LogError(
                        "Paso 5 FALLÓ — client_code={CardCode}, doc_num={DocNum}: se esperaba " +
                        "DocEntry={Esperado} y se obtuvo {Obtenido}.",
                        cardCode,
                        docNum,
                        docEntryEsperado,
                        resultado.DocEntry);
                }
            }
        }
        else
        {
            _logger.LogWarning(
                "Paso 5 — doc_entry y doc_num coinciden en TODAS las facturas de esta base, así que " +
                "no se puede demostrar la distinción con datos de aquí. Habría que confirmarlo " +
                "contra PRD_DINAS.");
        }

        // (2) ¿Cuántas series hay en OINV? Si es más de una, la ambigüedad de
        //     DocNum deja de ser teórica.
        await using (var cmd = new SqlCommand(
            $"""
            SELECT series, COUNT(*) AS facturas, MIN(doc_num), MAX(doc_num)
            FROM {DocEntryResolver.ViewName}
            GROUP BY series ORDER BY series
            """, connection))
        {
            cmd.CommandTimeout = _sqlOptions.CommandTimeoutSeconds;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            var series = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                series++;
                _logger.LogInformation(
                    "Paso 6 — series={Series}: {Facturas} facturas, doc_num {Min}–{Max}.",
                    reader.IsDBNull(0) ? "(null)" : reader.GetValue(0),
                    reader.GetValue(1),
                    reader.GetValue(2),
                    reader.GetValue(3));
            }

            _logger.LogInformation("Paso 6 — total de series en uso: {Total}.", series);
        }

        // (3) ¿Hay parejas (client_code, doc_num) repetidas al incluir TODAS las
        //     facturas? Es el número que decide si hay que meter la serie en el
        //     contrato del middleware.
        var repetidas = await EscalarIntAsync(
            connection,
            $"""
            SELECT COUNT(*) FROM (
                SELECT client_code, doc_num
                FROM {DocEntryResolver.ViewName}
                GROUP BY client_code, doc_num
                HAVING COUNT(*) > 1
            ) X
            """,
            cancellationToken).ConfigureAwait(false);

        if (repetidas == 0)
        {
            _logger.LogInformation(
                "Paso 7 OK — 0 parejas (client_code, doc_num) repetidas sobre TODAS las facturas: " +
                "la pareja identifica el documento sin ambigüedad en esta base.");
        }
        else
        {
            _logger.LogWarning(
                "Paso 7 — hay {Repetidas} pareja(s) (client_code, doc_num) repetidas. La ambigüedad " +
                "de series es REAL: hay que exponer la serie en el contrato antes de aplicar pagos.",
                repetidas);
        }
    }

    private async Task<(string CardCode, int DocNum, int DocEntry)?> BuscarMuestraAsync(
        SqlConnection connection,
        string filtro,
        CancellationToken cancellationToken)
    {
        // El filtro es texto fijo definido en este archivo, nunca entrada externa.
        await using var cmd = new SqlCommand(
            $"""
            SELECT TOP 1 client_code, doc_num, doc_entry
            FROM {DocEntryResolver.ViewName}
            WHERE {filtro}
            """, connection);
        cmd.CommandTimeout = _sqlOptions.CommandTimeoutSeconds;

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (reader.GetString(0),
                Convert.ToInt32(reader.GetValue(1)),
                Convert.ToInt32(reader.GetValue(2)));
    }

    private async Task<int> EscalarIntAsync(
        SqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(sql, connection);
        cmd.CommandTimeout = _sqlOptions.CommandTimeoutSeconds;
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
    }
}
