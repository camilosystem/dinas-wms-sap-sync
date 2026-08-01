using System.Text.Json;
using System.Text.Json.Serialization;

namespace DinasWms.SapSync.ServiceLayer.Invoices;

/// <summary>
/// Payload de <c>POST /Invoices</c> (y de <c>POST /Drafts</c>, que usa el mismo
/// tipo <c>Document</c> con <see cref="DocObjectCode"/> = <c>oInvoices</c>).
/// </summary>
/// <remarks>
/// La forma sale de inspeccionar facturas REALES de SUPPORT_DINAS, no de la
/// documentación ni del <c>$metadata</c> — ahí las 315 propiedades de la
/// cabecera y las 246 de la línea aparecen como opcionales, así que no dice
/// nada sobre qué es obligatorio.
///
/// El molde es la factura DocNum 7959: <c>dDocument_Items</c>, líneas con
/// <c>BaseType = -1</c> (sin documento base) y almacén 01. Es exactamente la
/// factura STANDALONE que necesita el WMS.
///
/// Campos que a propósito NO se envían:
///   · Series      — todas las facturas reales usan la 4, pero se deja que SAP
///                   tome la serie por defecto del usuario (igual que en pagos).
///   · DocCurrency — SAP toma la moneda del socio de negocio ($ en toda la base).
///   · DocDueDate  — la calcula SAP con las condiciones de pago del cliente.
///   · LineTotal   — lo calcula SAP; mandarlo sería pelear con su redondeo.
///   · BaseType/BaseEntry/BaseLine — no hay Orden de Venta base. Nunca.
/// </remarks>
public sealed class InvoicePayload
{
    /// <summary>Se usa <see cref="decimal"/> y no double para el dinero.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Factura con ítems de inventario. Enum <c>BoDocumentTypes</c>: el otro
    /// valor, <c>dDocument_Service</c>, es el de las facturas contra cuenta
    /// contable (migración de SAGE, notas manuales) y no aplica acá.
    /// </summary>
    [JsonPropertyName("DocType")]
    public string DocType { get; set; } = "dDocument_Items";

    /// <summary>
    /// Solo para <c>POST /Drafts</c>: qué clase de documento es el borrador.
    /// En <c>POST /Invoices</c> va en null y no se serializa.
    /// </summary>
    [JsonPropertyName("DocObjectCode")]
    public string? DocObjectCode { get; set; }

    [JsonPropertyName("CardCode")]
    public string CardCode { get; set; } = "";

    /// <summary>Fecha del documento, formato <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    /// <summary>
    /// Rastro del documento de origen. <b>Siempre lleva el <c>order_uuid</c></b>
    /// de la tarea — es lo que hace posible el chequeo anti-duplicado.
    /// Construirlo con <see cref="BuildComments"/>, no a mano.
    /// </summary>
    [JsonPropertyName("Comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("DocumentLines")]
    public List<InvoiceLine> DocumentLines { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Largo del campo <c>Comments</c> en SAP (<c>OINV</c>).</summary>
    private const int CommentsMaxLength = 254;

    /// <summary>
    /// Arma el <c>Comments</c> de una factura. El <c>order_uuid</c> es
    /// obligatorio y va SIEMPRE de primero, por la misma razón que el
    /// <c>payment_uuid</c> en los pagos: el campo se recorta, y el uuid es lo
    /// último que se puede perder.
    /// </summary>
    /// <param name="orderUuid">
    /// El <c>order_uuid</c> del snapshot, que en esta cola vale lo mismo que el
    /// <c>document_uuid</c> de la tarea: la factura no tiene uuid propio.
    /// </param>
    public static string BuildComments(string orderUuid, string? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(orderUuid);

        var comments = $"order_uuid={orderUuid}";

        if (!string.IsNullOrWhiteSpace(context))
        {
            comments = $"{comments} | {context}";
        }

        return comments.Length <= CommentsMaxLength ? comments : comments[..CommentsMaxLength];
    }
}

/// <summary>
/// Una línea de la factura. Complex type <c>DocumentLine</c>.
/// </summary>
/// <remarks>
/// El precio del WMS es FINAL y CONGELADO: SAP no debe recalcularlo contra su
/// lista de precios. <b>El campo que manda es <see cref="UnitPrice"/>, no
/// <see cref="Price"/></b>, y eso está VERIFICADO con borradores reales contra
/// SUPPORT_DINAS (31-jul-2026):
///
///   · <c>UnitPrice 12.34 + DiscountPercent 25</c> → SAP guardó
///     <c>Price 9.26</c>, <c>PriceSource dpsManual</c>. Respetado.
///   · <c>UnitPrice 33.25 + DiscountPercent 100</c> → <c>Price 0</c>. Respetado.
///   · <c>UnitPrice 0</c> → <c>Price 0</c>. Respetado (el 0 NO se toma como
///     "no me mandaron nada").
///   · <c>Price 7.77</c> SIN <c>UnitPrice</c> → <b>IGNORADO</b>: SAP puso el
///     precio de la lista activa del cliente (35.50) y marcó
///     <c>PriceSource dpsActivePriceList</c>. Le habría facturado al cliente
///     un precio que el WMS nunca autorizó, sin avisar.
/// </remarks>
public sealed class InvoiceLine
{
    [JsonPropertyName("ItemCode")]
    public string ItemCode { get; set; } = "";

    /// <summary>
    /// Cantidad en <b>unidad de venta</b> (CASE / BAG / DISPLAY), que es como
    /// facturan los documentos reales de esta base.
    /// </summary>
    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; set; }

    /// <summary>Precio ANTES de descuento.</summary>
    [JsonPropertyName("UnitPrice")]
    public decimal? UnitPrice { get; set; }

    /// <summary>
    /// Precio DESPUÉS de descuento. <b>Lo deriva SAP; no sirve para imponer un
    /// precio</b> (ver el remarks de la clase). Existe solo para poder leer lo
    /// que SAP calculó y contrastarlo con lo que esperaba el WMS.
    /// </summary>
    [JsonPropertyName("Price")]
    public decimal? Price { get; set; }

    [JsonPropertyName("DiscountPercent")]
    public decimal? DiscountPercent { get; set; }

    /// <summary>Almacén del que sale la mercancía. 01 = JAMAICA, el del WMS.</summary>
    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    /// <summary>
    /// Único código de impuesto de venta de la empresa: <c>Exempt</c>, tasa 0.
    /// Se manda explícito porque SAP no tiene de dónde derivarlo (los ítems no
    /// tienen <c>SalesVATGroup</c> y los socios tienen <c>VatGroup</c> nulo).
    /// </summary>
    [JsonPropertyName("TaxCode")]
    public string? TaxCode { get; set; }

    /// <summary>
    /// De qué ubicaciones sale la mercancía. Obligatorio en el almacén 01 cuando
    /// el ítem tiene stock en más de un bin: con
    /// <c>AutoAllocOnIssue = whsBinSingleChoiceOnly</c>, SAP resuelve solo si hay
    /// una única opción y rechaza el documento si hay varias
    /// (<c>1470000341 - Fully allocate item … to bin locations in warehouse …</c>).
    /// </summary>
    [JsonPropertyName("DocumentLinesBinAllocations")]
    public List<InvoiceBinAllocation>? DocumentLinesBinAllocations { get; set; }
}

/// <summary>
/// Una asignación de bin. Complex type <c>DocumentLinesBinAllocation</c>.
/// </summary>
public sealed class InvoiceBinAllocation
{
    /// <summary>
    /// Clave interna del bin (<c>OBIN.AbsEntry</c>), no el <c>BinCode</c>. Se
    /// resuelve localmente contra <c>BinLocations</c>.
    /// </summary>
    [JsonPropertyName("BinAbsEntry")]
    public int BinAbsEntry { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal Quantity { get; set; }

    /// <summary>
    /// A qué línea del documento pertenece esta asignación: el <c>LineNum</c>
    /// (base 0). Como el payload no manda <c>LineNum</c> y deja que SAP los
    /// asigne por orden, es el índice de la línea en <c>DocumentLines</c>.
    /// </summary>
    [JsonPropertyName("BaseLineNumber")]
    public int BaseLineNumber { get; set; }
}
