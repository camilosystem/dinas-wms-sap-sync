using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Scheduling;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Scheduler de ventanas: dispara un ciclo en cada slot del horario configurado,
/// o cuando aparece el archivo de forzado.
/// </summary>
/// <remarks>
/// No hay solapamiento de ciclos por construcción: es un único bucle secuencial,
/// así que nunca hay dos sesiones de Service Layer abiertas por este worker. Si
/// un ciclo se pasa de largo y se saltan slots, se reporta y se sigue con el
/// próximo slot futuro — no se acumulan ejecuciones atrasadas.
/// </remarks>
public sealed class SyncSchedulerWorker : BackgroundService
{
    private readonly ISyncCycle _cycle;
    private readonly ForceRequestWatcher _forceWatcher;
    private readonly SchedulerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SyncSchedulerWorker> _logger;

    public SyncSchedulerWorker(
        ISyncCycle cycle,
        ForceRequestWatcher forceWatcher,
        IOptions<SchedulerOptions> options,
        TimeProvider timeProvider,
        ILogger<SyncSchedulerWorker> logger)
    {
        _cycle = cycle;
        _forceWatcher = forceWatcher;
        _options = options.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await EjecutarSchedulerAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado normal.
        }
        catch
        {
            // Si el scheduler muere, el host se detiene. El código de salida tiene
            // que reflejarlo: como Windows Service, salir con 0 le dice al SCM que
            // la parada fue normal y nadie se enteraría de la falla.
            Environment.ExitCode = 1;
            throw;
        }
    }

    private async Task EjecutarSchedulerAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        _options.Validate();

        _logger.LogInformation(
            "=== Scheduler de sincronización ===\n" +
            "  Horario activo:    {From} – {To} (hora local de esta máquina)\n" +
            "  Cada:              {Every} minutos\n" +
            "  Ciclo al arrancar: {RunOnStartup}\n" +
            "  Forzar ahora:      crear el archivo {ForceFile}",
            _options.ActiveFrom,
            _options.ActiveTo,
            _options.EveryMinutes,
            _options.RunOnStartup,
            _forceWatcher.FullPath);

        _forceWatcher.ClearStaleRequestAtStartup();

        // El chequeo de cancelación importa: si el servicio se detiene justo
        // durante el arranque, no queremos abrir una sesión contra SAP para nada.
        if (_options.RunOnStartup && !stoppingToken.IsCancellationRequested)
        {
            await EjecutarCicloAsync(SyncCycleTrigger.Startup, stoppingToken).ConfigureAwait(false);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var ahora = Ahora();

            var proximoSlot = IntervalSchedule.GetNextRun(
                ahora,
                _options.ParsedActiveFrom,
                _options.ParsedActiveTo,
                _options.EveryMinutes);

            if (proximoSlot is null)
            {
                // No debería pasar con configuración válida; si pasa, es mejor
                // detenerse ruidosamente que girar en vacío.
                _logger.LogError(
                    "No se pudo calcular el próximo ciclo con el horario configurado " +
                    "({From}–{To} cada {Every} min). El scheduler se detiene.",
                    _options.ActiveFrom,
                    _options.ActiveTo,
                    _options.EveryMinutes);
                return;
            }

            var espera = proximoSlot.Value - ahora;
            _logger.LogInformation(
                "Próximo ciclo: {Proximo:yyyy-MM-dd HH:mm:ss} (en {Espera}).",
                proximoSlot.Value,
                FormatearEspera(espera));

            var fueForzado = await EsperarHastaAsync(proximoSlot.Value, stoppingToken)
                .ConfigureAwait(false);

            if (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var trigger = fueForzado ? SyncCycleTrigger.Forced : SyncCycleTrigger.Scheduled;
            await EjecutarCicloAsync(trigger, stoppingToken).ConfigureAwait(false);

            if (!fueForzado)
            {
                ReportarSlotsPerdidos(proximoSlot.Value);
            }
        }

        _logger.LogInformation("Scheduler detenido.");
    }

    /// <summary>
    /// Espera hasta <paramref name="objetivo"/>, revisando el archivo de forzado
    /// periódicamente. Devuelve true si se interrumpió por un forzado.
    /// </summary>
    private async Task<bool> EsperarHastaAsync(DateTime objetivo, CancellationToken cancellationToken)
    {
        var intervaloRevision = TimeSpan.FromSeconds(_options.ForcePollSeconds);

        while (!cancellationToken.IsCancellationRequested)
        {
            if (_forceWatcher.TryConsumeRequest())
            {
                return true;
            }

            var restante = objetivo - Ahora();
            if (restante <= TimeSpan.Zero)
            {
                return false;
            }

            var tramo = restante < intervaloRevision ? restante : intervaloRevision;

            try
            {
                await Task.Delay(tramo, _timeProvider, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }

        return false;
    }

    private async Task EjecutarCicloAsync(SyncCycleTrigger trigger, CancellationToken cancellationToken)
    {
        try
        {
            var resultado = await _cycle.RunAsync(trigger, cancellationToken).ConfigureAwait(false);

            if (resultado.Success)
            {
                _logger.LogInformation(
                    "Ciclo completado: {Procesados} procesados, {Fallidos} fallidos.",
                    resultado.TotalProcessed,
                    resultado.TotalFailed);
            }
            else
            {
                _logger.LogError(
                    "Ciclo terminó con fallos: {Error} ({Procesados} procesados, {Fallidos} fallidos).",
                    resultado.ErrorMessage,
                    resultado.TotalProcessed,
                    resultado.TotalFailed);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Apagado durante un ciclo: normal, ya se registró en el ciclo.
        }
        catch (Exception ex)
        {
            // Red de seguridad: ningún fallo de un ciclo debe matar el scheduler.
            // El próximo slot se intenta igual.
            _logger.LogError(ex, "Fallo no controlado ejecutando el ciclo. El scheduler sigue activo.");
        }
    }

    private void ReportarSlotsPerdidos(DateTime slotEjecutado)
    {
        var perdidos = IntervalSchedule.CountMissedSlots(
            slotEjecutado,
            Ahora(),
            _options.ParsedActiveFrom,
            _options.ParsedActiveTo,
            _options.EveryMinutes);

        if (perdidos > 0)
        {
            _logger.LogWarning(
                "El ciclo duró más que el intervalo configurado: se saltaron {Perdidos} slot(s). " +
                "No se acumulan ejecuciones atrasadas; se sigue con el próximo slot futuro. " +
                "Si esto se repite, subir Scheduler:EveryMinutes.",
                perdidos);
        }
    }

    private DateTime Ahora() => _timeProvider.GetLocalNow().DateTime;

    private static string FormatearEspera(TimeSpan espera)
    {
        if (espera < TimeSpan.Zero)
        {
            espera = TimeSpan.Zero;
        }

        return espera.TotalHours >= 1
            ? $"{(int)espera.TotalHours}h {espera.Minutes}m"
            : espera.TotalMinutes >= 1
                ? $"{(int)espera.TotalMinutes}m {espera.Seconds}s"
                : $"{espera.Seconds}s";
    }
}
