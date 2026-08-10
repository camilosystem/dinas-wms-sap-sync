using System.Diagnostics;
using DinasWms.SapSync.Observability;
using DinasWms.SapSync.ServiceLayer;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Sync;

/// <summary>Ejecuta un ciclo de trabajo completo.</summary>
public interface ISyncCycle
{
    Task<SyncCycleResult> RunAsync(SyncCycleTrigger trigger, CancellationToken cancellationToken);
}

/// <summary>
/// Un ciclo = un <c>Login</c>, todos los pasos de documentos, y un <c>Logout</c>.
/// </summary>
/// <remarks>
/// Acá vive el patrón de sesión por ciclo: la sesión se abre al empezar y se
/// cierra al terminar, pase lo que pase con los pasos. Un paso que falla no
/// aborta los demás — un problema con notas de crédito no debe impedir que se
/// procesen los pagos de cartera — pero sí marca el ciclo como fallido.
/// </remarks>
public sealed class SyncCycle : ISyncCycle
{
    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly IReadOnlyList<IDocumentSyncStep> _steps;
    private readonly SyncCycleGate _gate;
    private readonly SyncStatus? _status;
    private readonly ILogger<SyncCycle> _logger;

    public SyncCycle(
        IServiceLayerSessionFactory sessionFactory,
        IEnumerable<IDocumentSyncStep> steps,
        SyncCycleGate gate,
        ILogger<SyncCycle> logger,
        SyncStatus? status = null)
    {
        _sessionFactory = sessionFactory;
        _steps = steps.ToArray();
        _gate = gate;
        _logger = logger;
        // Opcional para no obligar a las pruebas a construir instrumentación que
        // no están ejercitando.
        _status = status;
    }

    public async Task<SyncCycleResult> RunAsync(
        SyncCycleTrigger trigger,
        CancellationToken cancellationToken)
    {
        // El permiso se pide ANTES de abrir la sesión, y esa precedencia importa:
        // si se rechaza, no llega a existir una sesión de Service Layer que
        // cerrar ni una licencia consumida por nada.
        using var permiso = await _gate
            .TryEnterAsync(trigger.ToString(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (permiso is null)
        {
            return SyncCycleResult.Rejected(
                trigger, "ya hay un ciclo en curso; este disparo se descarta");
        }

        var reloj = Stopwatch.StartNew();
        _logger.LogInformation("--- Inicio de ciclo ({Trigger}) ---", trigger);

        try
        {
            await using var session = await _sessionFactory
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);

            // A partir de acá hay una licencia de SAP consumida. La pantalla
            // necesita poder decirlo, porque es el recurso que se comparte con
            // Attain.
            _status?.RegistrarSesionAbierta();

            if (_steps.Count == 0)
            {
                // Estado esperado en esta fase: el scheduler y la sesión ya están,
                // pero todavía no se construyó ningún tipo de documento. El ciclo
                // funciona como latido: confirma que SAP sigue autenticando.
                _logger.LogInformation(
                    "No hay pasos de sincronización registrados todavía. El ciclo solo " +
                    "verificó que la sesión de Service Layer autentica (Login/Logout).");

                reloj.Stop();
                return new SyncCycleResult(
                    trigger, true, reloj.Elapsed, 0, 0, Array.Empty<string>());
            }

            var resumenes = new List<string>(_steps.Count);
            var totalProcesados = 0;
            var totalFallidos = 0;
            var huboErrorDePaso = false;

            foreach (var step in _steps)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var resultado = await step
                        .ExecuteAsync(session, cancellationToken)
                        .ConfigureAwait(false);

                    totalProcesados += resultado.Processed;
                    totalFallidos += resultado.Failed;

                    var resumen =
                        $"{step.Name}: {resultado.Processed} procesados, {resultado.Failed} fallidos" +
                        (resultado.Message is null ? "" : $" — {resultado.Message}");
                    resumenes.Add(resumen);

                    if (resultado.Failed > 0)
                    {
                        _logger.LogWarning("Paso con fallos — {Resumen}", resumen);
                    }
                    else
                    {
                        _logger.LogInformation("Paso OK — {Resumen}", resumen);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // Aislamiento entre tipos de documento: se registra y se sigue
                    // con el siguiente paso.
                    huboErrorDePaso = true;
                    resumenes.Add($"{step.Name}: ERROR — {ex.Message}");
                    _logger.LogError(ex, "El paso {Step} falló. Se continúa con los demás.", step.Name);
                }
            }

            reloj.Stop();

            var exito = !huboErrorDePaso && totalFallidos == 0;
            return new SyncCycleResult(
                trigger, exito, reloj.Elapsed, totalProcesados, totalFallidos, resumenes,
                exito ? null : "Uno o más pasos reportaron fallos.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            reloj.Stop();
            _logger.LogInformation("Ciclo cancelado por apagado del servicio.");
            throw;
        }
        catch (ServiceLayerException ex)
        {
            reloj.Stop();
            _logger.LogError(
                "Ciclo abortado: no se pudo trabajar con Service Layer. {Message}", ex.Message);
            return SyncCycleResult.Failure(trigger, reloj.Elapsed, ex.Message);
        }
        catch (Exception ex)
        {
            reloj.Stop();
            _logger.LogError(ex, "Ciclo abortado por un error inesperado.");
            return SyncCycleResult.Failure(trigger, reloj.Elapsed, ex.Message);
        }
        finally
        {
            // En el finally y no al final del try: la sesión se cierra pase lo
            // que pase (el await using de arriba), así que el indicador tiene que
            // apagarse igual o la pantalla mostraría una licencia tomada para
            // siempre después de un ciclo que falló.
            _status?.RegistrarSesionCerrada();

            _logger.LogInformation(
                "--- Fin de ciclo ({Trigger}) en {Duracion} ---",
                trigger,
                reloj.Elapsed.ToString(@"hh\:mm\:ss\.fff"));
        }
    }
}
