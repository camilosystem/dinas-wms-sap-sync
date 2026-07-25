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

        // --- Paso 2: permiso de SELECT sobre OINV ------------------------
        // HAS_PERMS_BY_NAME responde sin necesidad de provocar el error: devuelve
        // 1 (tiene permiso), 0 (no tiene), o NULL (el objeto no existe o no es
        // visible para este login).
        await using (var permCmd = new SqlCommand(
            "SELECT HAS_PERMS_BY_NAME('OINV', 'OBJECT', 'SELECT')", connection))
        {
            permCmd.CommandTimeout = _sqlOptions.CommandTimeoutSeconds;
            var resultado = await permCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);

            if (resultado is null || resultado == DBNull.Value)
            {
                _logger.LogWarning(
                    "Paso 2 — HAS_PERMS_BY_NAME('OINV') devolvió NULL: la tabla no existe en " +
                    "{Base} o no es visible para este login.",
                    _sqlOptions.Database);
            }
            else if (Convert.ToInt32(resultado) == 1)
            {
                _logger.LogInformation("Paso 2 OK — el login tiene permiso SELECT sobre OINV.");
            }
            else
            {
                _logger.LogWarning(
                    "Paso 2 — el login NO tiene permiso SELECT sobre OINV. Se intenta la consulta " +
                    "igual para capturar el error exacto de SQL Server.");
            }
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
    }
}
