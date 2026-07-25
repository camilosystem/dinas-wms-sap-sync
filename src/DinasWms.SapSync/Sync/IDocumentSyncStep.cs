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
