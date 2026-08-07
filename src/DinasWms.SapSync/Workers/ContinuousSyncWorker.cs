using System.Diagnostics;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Modo continuo: sondea la cola del middleware cada pocos segundos y abre
/// sesión con SAP solo cuando hay trabajo real.
/// </summary>
/// <remarks>
/// Es lo que convierte al sincronizador en algo que opera solo. Una factura
/// confirmada en el Dashboard aparece en SAP en segundos, sin ventana horaria y
/// sin que nadie dispare nada.
///
/// Tres propiedades que importan y que no son accidentales:
///
///   · <b>En reposo no se abre sesión de SAP.</b> El sondeo pregunta solo al
///     middleware; la sesión se abre después de saber que hay tareas. Sin esto,
///     sondear cada 20 segundos se comería las licencias que comparte con Attain.
///   · <b>No hay solapamiento.</b> Un único bucle secuencial: el siguiente sondeo
///     empieza cuando el ciclo anterior terminó. Con 30 minutos entre ciclos el
///     solapamiento era improbable; con 20 segundos sería la norma.
///   · <b>Back-off ante fallos repetidos.</b> Si SAP o el middleware están caídos,
///     el intervalo se ensancha en vez de martillar y llenar el log de ruido que
///     tapa el problema real. Vuelve a la cadencia normal al primer éxito.
///
/// El disparo manual por archivo centinela sigue existiendo: fuerza un ciclo
/// aunque la sonda diga que no hay nada, que es lo que hace falta para reintentar
/// algo que quedó a medias.
/// </remarks>
public sealed class ContinuousSyncWorker : BackgroundService
{
    private readonly ISyncCycle _cycle;
    private readonly IReadOnlyList<IDocumentSyncStep> _steps;
    private readonly ForceRequestWatcher _forceWatcher;
    private readonly ContinuousOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ContinuousSyncWorker> _logger;

    public ContinuousSyncWorker(
        ISyncCycle cycle,
        IEnumerable<IDocumentSyncStep> steps,
        ForceRequestWatcher forceWatcher,
        IOptions<ContinuousOptions> options,
        TimeProvider timeProvider,
        ILogger<ContinuousSyncWorker> logger)
    {
        _cycle = cycle;
        _steps = steps.ToArray();
        _forceWatcher = forceWatcher;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await CorrerAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado normal.
        }
        catch
        {
            // Si el bucle muere, el host se detiene. Salir con 0 le diría al SCM
            // que la parada fue normal y nadie se enteraría.
            Environment.ExitCode = 1;
            throw;
        }
    }

    private async Task CorrerAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _options.Validate();

        _logger.LogInformation(
            "=== Sincronización continua ===\n" +
            "  Sondeo cada:      {Poll}s (sin abrir sesión de SAP)\n" +
            "  Back-off:         tras {Umbral} fallos seguidos, duplicando hasta {Max}s\n" +
            "  Pasos automáticos: {Pasos}\n" +
            "  Forzar ahora:     crear el archivo {ForceFile}\n" +
            "  Sin ventana horaria: corre las 24 horas.",
            _options.PollSeconds,
            _options.FailuresBeforeBackoff,
            _options.MaxBackoffSeconds,
            _steps.Count == 0 ? "(ninguno)" : string.Join(", ", _steps.Select(s => s.Name)),
            _forceWatcher.FullPath);

        if (_steps.Count == 0)
        {
            _logger.LogError(
                "No hay pasos automáticos registrados: el modo continuo no tendría nada que hacer. " +
                "Se detiene en vez de girar en vacío.");
            return;
        }

        _forceWatcher.ClearStaleRequestAtStartup();

        var fallosConsecutivos = 0;

        if (_options.RunOnStartup && !stoppingToken.IsCancellationRequested)
        {
            fallosConsecutivos = await EjecutarCicloAsync(
                SyncCycleTrigger.Startup, fallosConsecutivos, stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var espera = _options.CalcularEspera(fallosConsecutivos);

            var forzado = await EsperarAsync(espera, stoppingToken).ConfigureAwait(false);

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            if (forzado)
            {
                _logger.LogInformation("Forzado manual: se corre un ciclo sin consultar la cola.");
                fallosConsecutivos = await EjecutarCicloAsync(
                    SyncCycleTrigger.Forced, fallosConsecutivos, stoppingToken).ConfigureAwait(false);
                continue;
            }

            // --- La sonda barata ---------------------------------------------
            bool hayTrabajo;

            try
            {
                hayTrabajo = await HayTrabajoAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // No poder preguntar NO es lo mismo que no haber trabajo: cuenta
                // como fallo y ensancha el intervalo.
                fallosConsecutivos++;
                _logger.LogError(
                    ex,
                    "No se pudo consultar la cola del middleware ({Fallos} fallo(s) seguido(s)). " +
                    "Próximo intento en {Espera}s.",
                    fallosConsecutivos,
                    (int)_options.CalcularEspera(fallosConsecutivos).TotalSeconds);
                continue;
            }

            if (!hayTrabajo)
            {
                // Reposo: ni una sesión de SAP abierta. Se registra en Debug para
                // no llenar el log con un latido cada 20 segundos.
                _logger.LogDebug("Sin tareas pendientes. No se abre sesión con SAP.");
                fallosConsecutivos = 0;
                continue;
            }

            fallosConsecutivos = await EjecutarCicloAsync(
                SyncCycleTrigger.Scheduled, fallosConsecutivos, stoppingToken).ConfigureAwait(false);
        }

        _logger.LogInformation("Sincronización continua detenida.");
    }

    /// <summary>
    /// ¿Algún paso tiene trabajo? Se corta en el primero que diga que sí: para
    /// decidir si vale abrir sesión no hace falta preguntarle a todos.
    /// </summary>
    private async Task<bool> HayTrabajoAsync(CancellationToken cancellationToken)
    {
        foreach (var step in _steps)
        {
            if (await step.HasPendingWorkAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.LogInformation("{Paso} tiene tareas pendientes. Se abre sesión con SAP.", step.Name);
                return true;
            }
        }

        return false;
    }

    /// <summary>Corre un ciclo y devuelve el nuevo contador de fallos seguidos.</summary>
    private async Task<int> EjecutarCicloAsync(
        SyncCycleTrigger trigger,
        int fallosConsecutivos,
        CancellationToken cancellationToken)
    {
        var reloj = Stopwatch.StartNew();

        try
        {
            var resultado = await _cycle.RunAsync(trigger, cancellationToken).ConfigureAwait(false);
            reloj.Stop();

            if (resultado.Success)
            {
                _logger.LogInformation(
                    "Ciclo completado en {Duracion:0.0}s: {Procesados} procesados, {Fallidos} fallidos.",
                    reloj.Elapsed.TotalSeconds,
                    resultado.TotalProcessed,
                    resultado.TotalFailed);
                return 0;
            }

            var fallos = fallosConsecutivos + 1;
            _logger.LogError(
                "Ciclo con fallos ({Fallos} seguido(s)): {Error}. Próximo intento en {Espera}s.",
                fallos,
                resultado.ErrorMessage,
                (int)_options.CalcularEspera(fallos).TotalSeconds);
            return fallos;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return fallosConsecutivos;
        }
        catch (Exception ex)
        {
            // Red de seguridad: ningún fallo de un ciclo mata el bucle.
            var fallos = fallosConsecutivos + 1;
            _logger.LogError(
                ex,
                "Fallo no controlado en el ciclo ({Fallos} seguido(s)). El bucle sigue activo; " +
                "próximo intento en {Espera}s.",
                fallos,
                (int)_options.CalcularEspera(fallos).TotalSeconds);
            return fallos;
        }
    }

    /// <summary>
    /// Espera el intervalo, revisando el archivo de forzado. Devuelve true si se
    /// interrumpió por un forzado manual.
    /// </summary>
    private async Task<bool> EsperarAsync(TimeSpan total, CancellationToken cancellationToken)
    {
        // El centinela se revisa como mucho cada segundo: con el back-off en 5
        // minutos, un forzado no debería esperar 5 minutos para tomar efecto.
        var tramoMaximo = TimeSpan.FromSeconds(1);
        var restante = total;

        while (restante > TimeSpan.Zero && !cancellationToken.IsCancellationRequested)
        {
            if (_forceWatcher.TryConsumeRequest())
            {
                return true;
            }

            var tramo = restante < tramoMaximo ? restante : tramoMaximo;

            try
            {
                await Task.Delay(tramo, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            restante -= tramo;
        }

        return !cancellationToken.IsCancellationRequested && _forceWatcher.TryConsumeRequest();
    }
}
