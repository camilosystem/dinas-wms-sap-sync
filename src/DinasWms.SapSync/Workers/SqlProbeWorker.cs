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
/// Separa deliberadamente tres fallos que se confunden con facilidad:
/// no poder conectar/autenticar, poder conectar pero no tener <c>SELECT</c>
/// sobre <c>OINV</c>, y tener permiso pero que el documento no exista.
/// Se invoca con <c>--RunMode=SqlProbe --Probe:CardCode=… --Probe:DocNum=…</c>.
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
        var cardCode = _configuration["Probe:CardCode"];
        var docNumTexto = _configuration["Probe:DocNum"];

        _logger.LogInformation(
            "=== Probe de SQL / resolución de DocEntry ===\n" +
            "  Servidor: {Server}\n" +
            "  Base:     {Database}\n" +
            "  Usuario:  {UserName}\n" +
            "  Cifrado:  Encrypt={Encrypt}, TrustServerCertificate={Trust}",
            _sqlOptions.Server,
            _sqlOptions.Database,
            _sqlOptions.UserName,
            _sqlOptions.Encrypt,
            _sqlOptions.TrustServerCertificate);

        // --- Paso 1: conectar y autenticar -------------------------------
        await using var connection = await _connectionFactory
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("Paso 1 OK — conexión y autenticación establecidas.");

        await using (var infoCmd = new SqlCommand(
            "SELECT DB_NAME(), SUSER_SNAME(), USER_NAME(), @@VERSION", connection))
        {
            infoCmd.CommandTimeout = _sqlOptions.CommandTimeoutSeconds;
            await using var reader = await infoCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var version = reader.GetString(3).ReplaceLineEndings(" ");
                _logger.LogInformation(
                    "  Base efectiva: {Base} | login: {Login} | usuario en la base: {Usuario}\n" +
                    "  Versión: {Version}",
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    version.Length > 120 ? version[..120] + "…" : version);
            }
        }

        // --- Paso 2: permisos sobre la vista (y confirmación de que las tablas
        //             de SAP siguen cerradas, que es la red de seguridad) ------
        // HAS_PERMS_BY_NAME responde sin necesidad de provocar el error: devuelve
        // 1 (tiene permiso), 0 (no tiene), o NULL (el objeto no existe o no es
        // visible para este login).
        foreach (var objeto in new[] { DocEntryResolver.ViewName, "dbo.OINV", "dbo.ORIN" })
        {
            await using var permCmd = new SqlCommand(
                "SELECT HAS_PERMS_BY_NAME(@objeto, 'OBJECT', 'SELECT')", connection);
            permCmd.CommandTimeout = _sqlOptions.CommandTimeoutSeconds;
            permCmd.Parameters.Add("@objeto", System.Data.SqlDbType.NVarChar, 256).Value = objeto;

            var resultado = await permCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            var estado = resultado is null || resultado == DBNull.Value
                ? "NULL (no existe o no es visible para este login)"
                : Convert.ToInt32(resultado) == 1
                    ? "SÍ"
                    : "NO";

            _logger.LogInformation("Paso 2 — SELECT sobre {Objeto}: {Estado}", objeto, estado);
        }

        // --- Paso 3: resolver el DocEntry pedido -------------------------
        if (string.IsNullOrWhiteSpace(cardCode) || string.IsNullOrWhiteSpace(docNumTexto))
        {
            _logger.LogWarning(
                "Paso 3 omitido: no se indicaron Probe:CardCode y Probe:DocNum. " +
                "Ejemplo: --Probe:CardCode=C100012 --Probe:DocNum=6152");
            return;
        }

        if (!int.TryParse(docNumTexto, out var docNum))
        {
            _logger.LogError("Probe:DocNum='{Valor}' no es un entero.", docNumTexto);
            Environment.ExitCode = 1;
            return;
        }

        var docEntry = await _resolver
            .ResolveInvoiceDocEntryAsync(cardCode, docNum, cancellationToken)
            .ConfigureAwait(false);

        if (docEntry is null)
        {
            _logger.LogWarning(
                "Paso 3 — la consulta funcionó, pero no existe factura con CardCode='{CardCode}' " +
                "y DocNum={DocNum} en {Base}.",
                cardCode,
                docNum,
                _sqlOptions.Database);
            Environment.ExitCode = 1;
            return;
        }

        _logger.LogInformation(
            "=== RESULTADO: CardCode={CardCode}, DocNum={DocNum} → DocEntry={DocEntry} ===",
            cardCode,
            docNum,
            docEntry);

        if (docEntry == docNum)
        {
            _logger.LogWarning(
                "Atención: para este documento DocEntry == DocNum ({Valor}), así que el caso NO " +
                "distingue una resolución correcta de devolver el doc_num por error. Se busca un " +
                "caso donde difieran para verificarlo de verdad.",
                docNum);
        }

        await VerificarConCasoDondeDifierenAsync(connection, cancellationToken).ConfigureAwait(false);
        await MedirAmbiguedadAsync(connection, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Busca una factura donde <c>doc_entry != doc_num</c> y la resuelve. Es la
    /// verificación que realmente demuestra que se devuelve el DocEntry.
    /// </summary>
    private async Task VerificarConCasoDondeDifierenAsync(
        SqlConnection connection,
        CancellationToken cancellationToken)
    {
        string? cardCode = null;
        int docNum = 0, docEntryEsperado = 0;

        await using (var cmd = new SqlCommand(
            $"""
            SELECT TOP 1 client_code, doc_num, doc_entry
            FROM {DocEntryResolver.ViewName}
            WHERE doc_type = 'INVOICE' AND doc_entry <> doc_num
            """, connection))
        {
            cmd.CommandTimeout = _sqlOptions.CommandTimeoutSeconds;
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cardCode = reader.GetString(0);
                docNum = Convert.ToInt32(reader.GetValue(1));
                docEntryEsperado = Convert.ToInt32(reader.GetValue(2));
            }
        }

        if (cardCode is null)
        {
            _logger.LogWarning(
                "Paso 4 — no hay ninguna factura abierta donde doc_entry difiera de doc_num en " +
                "esta base, así que no se puede demostrar la distinción con datos reales de aquí.");
            return;
        }

        var resuelto = await _resolver
            .ResolveInvoiceDocEntryAsync(cardCode, docNum, cancellationToken)
            .ConfigureAwait(false);

        if (resuelto == docEntryEsperado)
        {
            _logger.LogInformation(
                "Paso 4 OK — caso donde difieren: client_code={CardCode}, doc_num={DocNum} → " +
                "DocEntry={DocEntry}. El resolver devuelve el DocEntry, no el DocNum.",
                cardCode,
                docNum,
                resuelto);
        }
        else
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "Paso 4 FALLÓ — para client_code={CardCode}, doc_num={DocNum} se esperaba " +
                "DocEntry={Esperado} y se obtuvo {Obtenido}.",
                cardCode,
                docNum,
                docEntryEsperado,
                resuelto);
        }
    }

    /// <summary>
    /// Mide si la ambigüedad de series es real en estos datos: cuántas parejas
    /// (client_code, doc_num) aparecen más de una vez entre las facturas.
    /// </summary>
    private async Task MedirAmbiguedadAsync(SqlConnection connection, CancellationToken cancellationToken)
    {
        await using var cmd = new SqlCommand(
            $"""
            SELECT COUNT(*) FROM (
                SELECT client_code, doc_num
                FROM {DocEntryResolver.ViewName}
                WHERE doc_type = 'INVOICE'
                GROUP BY client_code, doc_num
                HAVING COUNT(*) > 1
            ) X
            """, connection);
        cmd.CommandTimeout = _sqlOptions.CommandTimeoutSeconds;

        var colisiones = Convert.ToInt32(await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));

        if (colisiones == 0)
        {
            _logger.LogInformation(
                "Paso 5 OK — no hay ninguna pareja (client_code, doc_num) repetida entre las " +
                "facturas abiertas: hoy client_code+doc_num identifica el documento sin ambigüedad.");
        }
        else
        {
            _logger.LogWarning(
                "Paso 5 — hay {Colisiones} pareja(s) (client_code, doc_num) repetidas entre las " +
                "facturas abiertas. La ambigüedad de series es REAL en estos datos y hay que " +
                "resolverla en el contrato antes de aplicar pagos.",
                colisiones);
        }
    }
}
