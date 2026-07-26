using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Middleware;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Sondeo de solo lectura del middleware: pide la cola de tareas pendientes y
/// muestra la respuesta literal.
/// </summary>
/// <remarks>
/// Existe por la misma razón que el sondeo de Service Layer: la forma exacta del
/// contrato se confirma contra una respuesta real antes de escribir los DTOs. El
/// documento de contrato es punto de partida, no la verdad.
///
/// Uso: <c>--RunMode=MiddlewareProbe [--Probe:Path=admin/sap-sync/account-payments/pending]</c>
/// </remarks>
public sealed class MiddlewareProbeWorker : BackgroundService
{
    private readonly IMiddlewareClient _client;
    private readonly MiddlewareOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<MiddlewareProbeWorker> _logger;

    public MiddlewareProbeWorker(
        IMiddlewareClient client,
        IOptions<MiddlewareOptions> options,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<MiddlewareProbeWorker> logger)
    {
        _client = client;
        _options = options.Value;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            var ruta = _configuration["Probe:Path"] ?? "admin/sap-sync/account-payments/pending";

            _logger.LogInformation(
                "=== Sondeo del middleware ===\n  Base: {Base}\n  Ruta: {Ruta}\n  Credencial: {Cred}",
                _options.BaseUrl,
                ruta,
                string.IsNullOrWhiteSpace(_options.ApiKey)
                    ? "(ninguna configurada)"
                    : $"header {_options.ApiKeyHeader}, presente");

            var (status, body) = await _client.GetAsync(ruta, stoppingToken).ConfigureAwait(false);

            _logger.LogInformation(
                "=== RESPUESTA LITERAL ({Codigo} {Status}) ===\n{Body}",
                (int)status,
                status,
                string.IsNullOrWhiteSpace(body) ? "(cuerpo vacío)" : body);

            if (status != System.Net.HttpStatusCode.OK)
            {
                Environment.ExitCode = 1;
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado normal.
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            _logger.LogError(ex, "SONDEO DEL MIDDLEWARE FALLIDO. {Message}", ex.Message);
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }
}
