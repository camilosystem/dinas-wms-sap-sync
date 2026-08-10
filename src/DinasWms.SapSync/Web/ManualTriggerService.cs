using DinasWms.SapSync.Observability;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Web;

/// <summary>Cómo salió un disparo manual.</summary>
public sealed record DisparoManual(
    string Id,
    string Tipo,
    string Usuario,
    DateTimeOffset Iniciado,
    DateTimeOffset? Terminado,
    string Estado,
    string? Detalle,
    int Integrados,
    int Fallidos);

/// <summary>
/// Dispara un ciclo a mano desde la pantalla, en segundo plano.
/// </summary>
/// <remarks>
/// <b>Corre por el mismo camino que el bucle automático:</b>
/// <see cref="ISyncCycle.RunAsync"/>, que es donde vive el <c>using</c> del
/// permiso. No estrena un camino propio a propósito — un atajo que abriera
/// sesión por su cuenta se saltearía el portón y rompería la garantía de que
/// nunca hay dos ciclos contra SAP.
///
/// <para>
/// La tarea de fondo envuelve todo en try/catch además del <c>using</c> del
/// ciclo: si reventara sin capturar, sería una excepción no observada en un
/// <c>Task</c> huérfano, invisible en el log y sin nadie a quien reportarle.
/// </para>
/// </remarks>
public sealed class ManualTriggerService
{
    /// <summary>
    /// Tipos que se pueden disparar, y a qué paso corresponden. El nombre
    /// público es el que ve la pantalla; el interno es el <c>Name</c> del paso.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> TiposDisponibles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["facturas"] = "OrderInvoices",
            ["pagos"] = "IncomingPayments",
            ["notas-credito"] = "CreditNotes",
        };

    private readonly ISyncCycle _cycle;
    private readonly SyncCycleGate _gate;
    private readonly SyncStatus _status;
    private readonly ILogger<ManualTriggerService> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly object _candado = new();

    private DisparoManual? _ultimo;

    public ManualTriggerService(
        ISyncCycle cycle,
        SyncCycleGate gate,
        SyncStatus status,
        TimeProvider timeProvider,
        ILogger<ManualTriggerService> logger)
    {
        _cycle = cycle;
        _gate = gate;
        _status = status;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>El último disparo manual, para que la pantalla lo muestre.</summary>
    public DisparoManual? Ultimo
    {
        get { lock (_candado) { return _ultimo; } }
    }

    /// <summary>
    /// Lanza el ciclo en segundo plano. Devuelve null si ya hay uno en curso.
    /// </summary>
    /// <remarks>
    /// No se espera a que termine: un ciclo con la cola cargada puede tardar
    /// minutos, y un request HTTP colgado sería peor experiencia y peor
    /// diagnóstico. La pantalla sigue el avance por el estado en vivo.
    /// </remarks>
    public DisparoManual? Disparar(string tipo, string usuario, CancellationToken cancellationToken)
    {
        if (!TiposDisponibles.TryGetValue(tipo, out var nombrePaso))
        {
            throw new ArgumentException($"Tipo de disparo desconocido: '{tipo}'.", nameof(tipo));
        }

        // Chequeo temprano para poder responder 409 al instante. Hay una ventana
        // entre esto y el TryEnter del ciclo; si alguien se cuela ahí, el ciclo
        // devuelve rechazado y queda registrado como tal. El chequeo es para dar
        // una respuesta rápida, no para garantizar nada — la garantía es el
        // portón.
        if (_gate.EnUso)
        {
            return null;
        }

        var disparo = new DisparoManual(
            Guid.NewGuid().ToString("N")[..8],
            tipo,
            usuario,
            _timeProvider.GetLocalNow(),
            null,
            "EN_CURSO",
            null,
            0,
            0);

        lock (_candado)
        {
            _ultimo = disparo;
        }

        _logger.LogWarning(
            "DISPARO MANUAL {Id}: {Usuario} pidió correr '{Tipo}' ({Paso}). Esto escribe en SAP.",
            disparo.Id,
            usuario,
            tipo,
            nombrePaso);

        _ = Task.Run(() => EjecutarAsync(disparo, nombrePaso, cancellationToken), CancellationToken.None);

        return disparo;
    }

    private async Task EjecutarAsync(
        DisparoManual disparo,
        string nombrePaso,
        CancellationToken cancellationToken)
    {
        try
        {
            var resultado = await _cycle
                .RunAsync(SyncCycleTrigger.Forced, cancellationToken, [nombrePaso])
                .ConfigureAwait(false);

            _status.RegistrarCicloTerminado(_timeProvider.GetLocalNow(), resultado);

            var estado = resultado.RejectedByConcurrency
                ? "RECHAZADO"
                : resultado.Success ? "OK" : "CON_FALLOS";

            Terminar(disparo, estado, resultado.ErrorMessage, resultado.TotalProcessed, resultado.TotalFailed);

            _logger.LogInformation(
                "DISPARO MANUAL {Id}: terminó {Estado} — {Integrados} integrados, {Fallidos} fallidos.",
                disparo.Id,
                estado,
                resultado.TotalProcessed,
                resultado.TotalFailed);
        }
        catch (Exception ex)
        {
            // Sin este catch la excepción moriría en un Task huérfano: nadie la
            // vería y la pantalla mostraría "EN_CURSO" para siempre.
            _logger.LogError(ex, "DISPARO MANUAL {Id}: falló de forma inesperada.", disparo.Id);
            Terminar(disparo, "ERROR", ex.Message, 0, 0);
        }
    }

    private void Terminar(DisparoManual disparo, string estado, string? detalle, int ok, int fallidos)
    {
        lock (_candado)
        {
            _ultimo = disparo with
            {
                Terminado = _timeProvider.GetLocalNow(),
                Estado = estado,
                Detalle = detalle,
                Integrados = ok,
                Fallidos = fallidos,
            };
        }
    }
}
