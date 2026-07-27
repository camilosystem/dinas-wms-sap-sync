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
            // Varias rutas en un solo login, para no gastar un token por consulta.
            var rutas = _configuration.GetSection("Probe:Paths")
                .GetChildren()
                .Select(c => c.Value)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToList();

            if (rutas.Count == 0)
            {
                rutas.Add(_configuration["Probe:Path"] ?? "admin/sap-sync/account-payments/pending");
            }

            _logger.LogInformation(
                "=== Sondeo del middleware ===\n  Base: {Base}\n  Usuario: {Usuario}\n  Rutas: {Cuantas}",
                _options.BaseUrl,
                _options.UserName,
                rutas.Count);

            await _client.LoginAsync(stoppingToken).ConfigureAwait(false);

            foreach (var ruta in rutas)
            {
                _logger.LogInformation("GET {Ruta} …", ruta);

                var (status, body) = await _client.GetAsync(ruta, stoppingToken).ConfigureAwait(false);

                var cuerpo = string.IsNullOrWhiteSpace(body)
                    ? "(cuerpo vacío)"
                    : body.Length > 12000 ? body[..12000] + "\n… (truncado)" : body;

                _logger.LogInformation(
                    "  → RESPUESTA LITERAL ({Codigo} {Status}), {Bytes:N0} bytes:\n{Body}",
                    (int)status,
                    status,
                    body.Length,
                    cuerpo);

                if (status != System.Net.HttpStatusCode.OK)
                {
                    Environment.ExitCode = 1;
                }
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
