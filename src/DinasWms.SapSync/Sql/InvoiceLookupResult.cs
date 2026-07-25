namespace DinasWms.SapSync.Sql;

/// <summary>
/// Qué se encontró al buscar una factura. Cada caso pide una reacción distinta
/// del sincronizador, y confundirlos tiene consecuencias distintas.
/// </summary>
public enum InvoiceLookupOutcome
{
    /// <summary>
    /// Factura abierta y vigente. Se puede aplicar el pago.
    /// </summary>
    Resolved,

    /// <summary>
    /// No existe ninguna factura con ese cliente y número.
    /// <b>Error de datos</b>: hay que reportarlo e investigar, porque significa
    /// que el middleware envió una referencia que SAP no reconoce.
    /// </summary>
    NotFound,

    /// <summary>
    /// La factura existe pero está cerrada (<c>DocStatus = 'C'</c>).
    /// <b>Duplicado benigno</b>: lo normal es que el pago ya se haya aplicado, así
    /// que se descarta sin ruido.
    /// </summary>
    /// <remarks>
    /// Ojo con el nombre corto "ya pagada": en SAP una factura también se cierra
    /// por una nota de crédito o por cierre manual, no solo por pago completo.
    /// Por eso el resultado trae <c>OpenAmount</c>: si está cerrada con saldo
    /// distinto de cero, no se cerró por pago y merece mirarse.
    /// </remarks>
    Closed,

    /// <summary>
    /// La factura está anulada (<c>CANCELED = 'Y'</c>).
    /// <b>Error de negocio</b>: no se debe aplicar un pago contra una factura
    /// anulada, y no es un duplicado benigno — algo está mal en el origen.
    /// </summary>
    Canceled,
}

/// <summary>Resultado de buscar una factura por cliente + número.</summary>
public sealed record InvoiceLookupResult(
    InvoiceLookupOutcome Outcome,
    string CardCode,
    int DocNum,
    int? DocEntry = null,
    int? Series = null,
    string? DocStatus = null,
    bool? IsCanceled = null,
    decimal? DocTotal = null,
    decimal? PaidAmount = null,
    decimal? OpenAmount = null)
{
    /// <summary>
    /// True solo cuando hay un <c>DocEntry</c> utilizable para aplicar un pago.
    /// </summary>
    public bool CanApplyPayment => Outcome == InvoiceLookupOutcome.Resolved && DocEntry is not null;

    public static InvoiceLookupResult NotFound(string cardCode, int docNum) =>
        new(InvoiceLookupOutcome.NotFound, cardCode, docNum);
}
