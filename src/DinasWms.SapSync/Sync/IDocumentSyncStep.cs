using DinasWms.SapSync.ServiceLayer;

namespace DinasWms.SapSync.Sync;

/// <summary>
/// Un tipo de documento a integrar dentro de un ciclo (pagos de cartera, notas
/// de crédito, facturas, …). Es la costura para las fases siguientes del
/// roadmap: el scheduler y el ciclo ya no cambian al agregar uno.
/// </summary>
/// <remarks>
/// A propósito no dice nada sobre la forma de los payloads de Service Layer —
/// eso se define por ensayo y error verificado, un tipo de documento a la vez.
/// Todavía no hay ninguna implementación registrada.
/// </remarks>
public interface IDocumentSyncStep
{
    /// <summary>Nombre para logs, ej. "IncomingPayments".</summary>
    string Name { get; }

    /// <summary>
    /// ¿Lo corre el bucle automático, o solo se dispara a mano?
    /// </summary>
    /// <remarks>
    /// Las notas de crédito son manual-only por decisión de negocio: se
    /// disparan desde la pantalla, nunca solas. Tenerlo como propiedad del paso
    /// —y no como una lista en el worker— hace que la decisión viva junto al
    /// paso y no se pueda perder al registrar uno nuevo.
    /// </remarks>
    bool RunsAutomatically => true;

    /// <summary>
    /// ¿Hay algo que hacer? <b>Sin abrir sesión de Service Layer.</b>
    /// </summary>
    /// <remarks>
    /// Es lo que hace viable consultar cada pocos segundos. Preguntarle al
    /// middleware es una llamada HTTP barata; una sesión de SAP no lo es, porque
    /// compite por licencias con Attain. Separar las dos preguntas permite
    /// sondear seguido y abrir sesión solo cuando hay trabajo real.
    ///
    /// Ante un fallo debe LANZAR, no devolver false: "no pude preguntar" y "no
    /// hay nada" son cosas distintas, y confundirlas haría que una caída del
    /// middleware se vea como reposo.
    /// </remarks>
    Task<bool> HasPendingWorkAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Procesa los documentos pendientes de este tipo usando la sesión del ciclo
    /// en curso. No debe hacer login ni logout: la sesión la administra el ciclo.
    /// </summary>
    Task<DocumentSyncStepResult> ExecuteAsync(
        ServiceLayerSession session,
        CancellationToken cancellationToken);
}

/// <summary>Resultado de un paso de sincronización.</summary>
/// <param name="Processed">Documentos integrados con éxito.</param>
/// <param name="Failed">Documentos que fallaron y quedan pendientes.</param>
/// <param name="Message">Detalle opcional para el log.</param>
public sealed record DocumentSyncStepResult(int Processed, int Failed, string? Message = null)
{
    public static DocumentSyncStepResult Nothing => new(0, 0);
}
