using System.Text.Json;
using System.Text.Json.Serialization;

namespace DinasWms.SapSync.ServiceLayer.CreditNotes;

/// <summary>
/// Payload de <c>POST /CreditNotes</c>.
/// </summary>
/// <remarks>
/// Toda la forma está verificada con borradores reales contra SUPPORT_DINAS
/// (agosto 2026), no supuesta:
///
///   · <b>Ligar a la factura</b>: basta con <c>BaseType 13</c> + <c>BaseEntry</c>
///     + <c>BaseLine</c> en la línea. SAP copia solo el <c>ItemCode</c>, el
///     precio y el <c>TaxCode</c> de la línea base.
///   · <b>Cuenta y almacén NO se pisan</b>: mandando <c>AccountCode 6020</c> y
///     <c>WarehouseCode 07</c> en una línea ligada a una factura del almacén 01,
///     SAP guardó 6020 y 07. El 4200 del histórico es el default aceptado, no un
///     límite del sistema.
///   · <b>El monto se impone con <c>UnitPrice</c></b>, igual que en facturas.
///     Con una línea base de 50.25 y <c>UnitPrice 7.77</c>, quedó 7.77.
///   · <b>En las líneas de SERVICIO la cantidad no multiplica</b>: con
///     <c>Quantity 2</c> y <c>UnitPrice 15.50</c> el <c>LineTotal</c> quedó en
///     15.50, no en 31.00. El monto ES el precio.
///
/// Series: las notas de crédito usan la 5 (las facturas la 4). No se envía —
/// SAP toma la serie por defecto, igual que en los otros documentos.
/// </remarks>
public sealed class CreditNotePayload
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary><c>dDocument_Items</c> o <c>dDocument_Service</c>, según el <c>doc_kind</c>.</summary>
    [JsonPropertyName("DocType")]
    public string DocType { get; set; } = "dDocument_Items";

    /// <summary>Solo para <c>POST /Drafts</c>. En <c>POST /CreditNotes</c> va nulo.</summary>
    [JsonPropertyName("DocObjectCode")]
    public string? DocObjectCode { get; set; }

    [JsonPropertyName("CardCode")]
    public string CardCode { get; set; } = "";

    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    /// <summary>
    /// Rastro de la solicitud. Lleva el <c>request_uuid</c> y el <c>doc_kind</c>
    /// — los dos, porque una solicitud puede producir DOS notas y el uuid solo no
    /// las distingue.
    /// </summary>
    [JsonPropertyName("Comments")]
    public string? Comments { get; set; }

    [JsonPropertyName("DocumentLines")]
    public List<CreditNoteLinePayload> DocumentLines { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Largo del campo <c>Comments</c> en SAP (<c>ORIN</c>).</summary>
    private const int CommentsMaxLength = 254;

    /// <summary>
    /// Arma el <c>Comments</c>. El <c>request_uuid</c> va SIEMPRE de primero y el
    /// <c>doc_kind</c> inmediatamente después: juntos son lo que identifica esta
    /// nota y no su hermana.
    /// </summary>
    /// <param name="invoiceDocNum">
    /// Factura de referencia. Va SIEMPRE que la solicitud la traiga, aunque la
    /// nota no quede ligada: cuando la factura está pagada SAP no admite el
    /// vínculo, y entonces este texto es el único rastro que queda del documento
    /// que originó el crédito.
    /// </param>
    public static string BuildComments(
        string requestUuid,
        string docKind,
        string? invoiceDocNum = null,
        string? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestUuid);
        ArgumentException.ThrowIfNullOrWhiteSpace(docKind);

        var comments = $"request_uuid={requestUuid} | doc_kind={docKind}";

        if (!string.IsNullOrWhiteSpace(invoiceDocNum))
        {
            comments = $"{comments} | invoice_doc_num={invoiceDocNum}";
        }

        if (!string.IsNullOrWhiteSpace(context))
        {
            comments = $"{comments} | {context}";
        }

        return comments.Length <= CommentsMaxLength ? comments : comments[..CommentsMaxLength];
    }
}

/// <summary>Una línea de la nota de crédito. Complex type <c>DocumentLine</c>.</summary>
public sealed class CreditNoteLinePayload
{
    /// <summary>Solo en líneas de ítems SIN factura base: si va ligada, lo copia SAP.</summary>
    [JsonPropertyName("ItemCode")]
    public string? ItemCode { get; set; }

    /// <summary>Texto libre de las líneas de servicio, que no tienen ítem.</summary>
    [JsonPropertyName("ItemDescription")]
    public string? ItemDescription { get; set; }

    [JsonPropertyName("Quantity")]
    public decimal? Quantity { get; set; }

    /// <summary>
    /// El monto que manda. En líneas de ítems es el precio unitario
    /// (<c>approved_amount / quantity</c>); en líneas de servicio es el monto
    /// completo, porque ahí la cantidad no multiplica.
    /// </summary>
    [JsonPropertyName("UnitPrice")]
    public decimal? UnitPrice { get; set; }

    /// <summary>4200 o 6020, según lo decidió el aprobador POR LÍNEA.</summary>
    [JsonPropertyName("AccountCode")]
    public string? AccountCode { get; set; }

    /// <summary>
    /// 01 (vendible) o 07 (DAMAGE). Nulo en servicio. El 07 no tiene ubicaciones
    /// activadas, así que esas líneas no llevan asignaciones de bin.
    /// </summary>
    [JsonPropertyName("WarehouseCode")]
    public string? WarehouseCode { get; set; }

    [JsonPropertyName("TaxCode")]
    public string? TaxCode { get; set; }

    // --- Vínculo con la factura ------------------------------------------
    // Los tres van juntos o no va ninguno.

    /// <summary>13 = factura de deudores (<c>oInvoices</c>).</summary>
    [JsonPropertyName("BaseType")]
    public int? BaseType { get; set; }

    /// <summary><c>DocEntry</c> de la factura, resuelto localmente por SQL.</summary>
    [JsonPropertyName("BaseEntry")]
    public int? BaseEntry { get; set; }

    /// <summary><c>LineNum</c> de la factura, elegido por el aprobador.</summary>
    [JsonPropertyName("BaseLine")]
    public int? BaseLine { get; set; }
}
