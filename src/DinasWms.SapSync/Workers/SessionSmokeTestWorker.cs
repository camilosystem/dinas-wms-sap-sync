using System.Net;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.ServiceLayer;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Worker temporal de esta fase de arranque: hace UN ciclo completo
/// (Login → lectura → Logout) contra Service Layer, reporta el resultado, y
/// detiene el host.
/// </summary>
/// <remarks>
/// Esto NO es el scheduler de ventanas — ese es la fase siguiente. Sirve para
/// validar que el módulo de sesión funciona de verdad contra SUPPORT_DINAS desde
/// esta máquina. Cuando exista el worker de ciclos reales, este se quita o se
/// queda como diagnóstico bajo un flag.
/// </remarks>
public sealed class SessionSmokeTestWorker : BackgroundService
{
    /// <summary>
    /// Sondas de lectura. Se prueban en orden y se reporta el resultado de cada
    /// una: el objetivo es confirmar que la cookie de sesión realmente autentica,
    /// no solo que el Login "pareció" exitoso.
    /// </summary>
    private static readonly ReadProbe[] Probes =
    [
        // OData plano contra una tabla chica: la sonda más neutral posible.
        new("GET Users?$top=1", () => new HttpRequestMessage(
            HttpMethod.Get, "Users?$select=UserCode,UserName&$top=1")),

        // Confirma la identidad de la Company DB contra la que quedó la sesión.
        // Es un service action de Service Layer, por eso va como POST.
        new("POST CompanyService_GetCompanyInfo", () => new HttpRequestMessage(
            HttpMethod.Post, "CompanyService_GetCompanyInfo")
        {
            Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
        }),
    ];

    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly ServiceLayerOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<SessionSmokeTestWorker> _logger;

    public SessionSmokeTestWorker(
        IServiceLayerSessionFactory sessionFactory,
        IOptions<ServiceLayerOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<SessionSmokeTestWorker> logger)
    {
        _sessionFactory = sessionFactory;
        _options = options.Value;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // No bloquear el arranque del host.
        await Task.Yield();

        try
        {
            await RunSmokeTestAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado normal.
        }
        catch (ServiceLayerException ex)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "PRUEBA DE SESIÓN FALLIDA. {Message}{Detalle}",
                ex.Message,
                ex.StatusCode is null ? "" : $" (HTTP {(int)ex.StatusCode})");
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            _logger.LogError(ex, "PRUEBA DE SESIÓN FALLIDA por un error inesperado.");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task RunSmokeTestAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "=== Prueba de sesión de Service Layer ===\n" +
            "  BaseUrl:   {BaseUrl}\n" +
            "  CompanyDB: {CompanyDB}\n" +
            "  UserName:  {UserName}\n" +
            "  Confiar en certificado autofirmado: {Trust}",
            _options.BaseUrl,
            _options.CompanyDB,
            _options.UserName,
            _options.TrustSelfSignedCertificate);

        await using var session = await _sessionFactory
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        var algunaSondaOk = false;

        foreach (var probe in Probes)
        {
            try
            {
                var (status, body) = await session
                    .SendForStringAsync(probe.RequestFactory, cancellationToken)
                    .ConfigureAwait(false);

                if (status is HttpStatusCode.OK or HttpStatusCode.NoContent)
                {
                    algunaSondaOk = true;
                    _logger.LogInformation(
                        "Sonda OK — {Probe} → {Status}. Cuerpo: {Body}",
                        probe.Name,
                        (int)status,
                        Truncate(body));
                }
                else
                {
                    _logger.LogWarning(
                        "Sonda con error — {Probe} → {Status}. Cuerpo: {Body}",
                        probe.Name,
                        (int)status,
                        Truncate(body));
                }
            }
            catch (ServiceLayerException ex)
            {
                _logger.LogWarning("Sonda con error — {Probe}: {Message}", probe.Name, ex.Message);
            }
        }

        if (!algunaSondaOk)
        {
            throw new ServiceLayerException(
                "El Login fue exitoso pero ninguna sonda de lectura autenticó. " +
                "Revisar los cuerpos de error de arriba antes de dar la sesión por buena.");
        }

        _logger.LogInformation(
            "=== Sesión validada. Cerrando (Logout) al liberar la sesión. ===");

        // El Logout ocurre en el DisposeAsync del 'await using'.
    }

    private static string Truncate(string value, int max = 1200)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(vacío)";
        }

        var flat = value.ReplaceLineEndings(" ");
        return flat.Length <= max ? flat : flat[..max] + $"… (truncado, {flat.Length} chars)";
    }

    private sealed record ReadProbe(string Name, Func<HttpRequestMessage> RequestFactory);
}
