using System.Text;
using DinasWms.SapSync.ServiceLayer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Exploración de Service Layer: baja el <c>$metadata</c> y documentos reales de
/// una entidad a archivos, para poder inspeccionarlos sin adivinar la forma de
/// los payloads.
/// </summary>
/// <remarks>
/// El proyecto se construye por ensayo y error verificado: la documentación de
/// Service Layer es punto de partida, pero lo que manda es lo que responde
/// SUPPORT_DINAS. Este worker existe para poder mirar eso de verdad.
///
/// Uso:
///   --RunMode=SlDiscovery --Discovery:Entity=IncomingPayments --Discovery:Top=3
///   --Discovery:OutputDir=C:\ruta   (opcional)
///   --Discovery:SkipMetadata=true   (el $metadata pesa varios MB)
/// </remarks>
public sealed class ServiceLayerDiscoveryWorker : BackgroundService
{
    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly IConfiguration _configuration;
    private readonly IHostEnvironment _environment;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<ServiceLayerDiscoveryWorker> _logger;

    public ServiceLayerDiscoveryWorker(
        IServiceLayerSessionFactory sessionFactory,
        IConfiguration configuration,
        IHostEnvironment environment,
        IHostApplicationLifetime lifetime,
        ILogger<ServiceLayerDiscoveryWorker> logger)
    {
        _sessionFactory = sessionFactory;
        _configuration = configuration;
        _environment = environment;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            await ExplorarAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado normal.
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            _logger.LogError(ex, "EXPLORACIÓN FALLIDA.");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task ExplorarAsync(CancellationToken cancellationToken)
    {
        var saltarMetadata = string.Equals(
            _configuration["Discovery:SkipMetadata"], "true", StringComparison.OrdinalIgnoreCase);

        var salida = _configuration["Discovery:OutputDir"]
            ?? Path.Combine(_environment.ContentRootPath, "discovery");
        Directory.CreateDirectory(salida);

        // Varias consultas en un solo ciclo, para no gastar un login por cada una:
        //   --Discovery:Queries:0="IncomingPayments?$filter=CashSum gt 0&$top=3"
        //   --Discovery:Queries:1="ChartOfAccounts?$top=5"
        // Si no se indican, cae al modo simple con Discovery:Entity.
        var consultas = _configuration.GetSection("Discovery:Queries")
            .GetChildren()
            .Select(c => c.Value)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!)
            .ToList();

        if (consultas.Count == 0)
        {
            var entidad = _configuration["Discovery:Entity"] ?? "IncomingPayments";
            var top = int.TryParse(_configuration["Discovery:Top"], out var t) ? t : 3;
            consultas.Add($"{entidad}?$top={top}&$orderby=DocEntry desc");
        }

        _logger.LogInformation(
            "=== Exploración de Service Layer ===\n  Consultas: {Cuantas}\n  Salida: {Salida}",
            consultas.Count,
            salida);

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        if (!saltarMetadata)
        {
            await DescargarAsync(
                session,
                "$metadata",
                Path.Combine(salida, "metadata.xml"),
                cancellationToken).ConfigureAwait(false);
        }

        // Documentos reales: es lo que muestra qué campos se usan de verdad, cosa
        // que el $metadata no dice (ahí todo parece opcional).
        for (var i = 0; i < consultas.Count; i++)
        {
            await DescargarAsync(
                session,
                consultas[i],
                Path.Combine(salida, $"query-{i:00}.json"),
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation("=== Exploración terminada. Archivos en {Salida} ===", salida);
    }

    private async Task DescargarAsync(
        ServiceLayerSession session,
        string rutaRelativa,
        string archivoDestino,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("GET {Ruta} …", rutaRelativa);

        var (status, body) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Get, rutaRelativa),
                cancellationToken)
            .ConfigureAwait(false);

        if (status != System.Net.HttpStatusCode.OK)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "  → {Status}. Cuerpo: {Body}",
                (int)status,
                body.Length > 800 ? body[..800] + "…" : body);
            return;
        }

        await File.WriteAllTextAsync(archivoDestino, body, Encoding.UTF8, cancellationToken)
            .ConfigureAwait(false);

        // Los cuerpos chicos van al log directamente: son el resultado que uno
        // quiere leer, no un archivo que abrir después.
        if (body.Length <= 4000)
        {
            _logger.LogInformation("  → 200, {Bytes:N0} bytes:\n{Body}", body.Length, body);
        }
        else
        {
            _logger.LogInformation(
                "  → 200, {Bytes:N0} bytes guardados en {Archivo}",
                body.Length,
                Path.GetFileName(archivoDestino));
        }
    }
}
