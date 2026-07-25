using System.Text.Json;
using System.Text.Json.Serialization;

namespace DinasWms.SapSync.ServiceLayer.Payments;

/// <summary>
/// Payload de <c>POST /IncomingPayments</c>.
/// </summary>
/// <remarks>
/// La forma sale de inspeccionar pagos REALES de SUPPORT_DINAS, no de la
/// documentación. Solo se incluyen los campos que los pagos existentes tienen
/// poblados para cada medio de pago; el resto se omite deliberadamente, para que
/// SAP aplique sus valores por defecto en vez de que nosotros los inventemos.
///
/// Campos que a propósito NO se envían todavía:
///   · Series        — los pagos reales usan 15, pero se deja que SAP tome la
///                     serie por defecto del usuario. Si falla, se agrega.
///   · DocCurrency   — SAP toma la moneda del socio de negocio.
///   · ControlAccount— SAP la resuelve (1200 en todos los pagos reales).
/// </remarks>
public sealed class IncomingPaymentPayload
{
    /// <summary>Se usa <see cref="decimal"/> y no double para los montos: es dinero.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>Pago de cliente. Enum <c>BoRcptTypes</c>.</summary>
    [JsonPropertyName("DocType")]
    public string DocType { get; set; } = "rCustomer";

    [JsonPropertyName("CardCode")]
    public string CardCode { get; set; } = "";

    /// <summary>Fecha del documento, formato <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("DocDate")]
    public string? DocDate { get; set; }

    // --- EFECTIVO ---------------------------------------------------------
    [JsonPropertyName("CashAccount")]
    public string? CashAccount { get; set; }

    [JsonPropertyName("CashSum")]
    public decimal? CashSum { get; set; }

    // --- TRANSFERENCIA ----------------------------------------------------
    [JsonPropertyName("TransferAccount")]
    public string? TransferAccount { get; set; }

    [JsonPropertyName("TransferSum")]
    public decimal? TransferSum { get; set; }

    [JsonPropertyName("TransferDate")]
    public string? TransferDate { get; set; }

    [JsonPropertyName("TransferReference")]
    public string? TransferReference { get; set; }

    /// <summary>
    /// Rastro del pago del middleware. <b>Siempre lleva el <c>payment_uuid</c></b>
    /// — es la forma acordada de auditar y rastrear contra SAP. Construirlo con
    /// <see cref="BuildRemarks"/>, no a mano.
    /// </summary>
    [JsonPropertyName("Remarks")]
    public string? Remarks { get; set; }

    /// <summary>Facturas a las que se aplica el pago.</summary>
    [JsonPropertyName("PaymentInvoices")]
    public List<IncomingPaymentInvoiceLine> PaymentInvoices { get; set; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Largo del campo <c>Comments</c> en SAP (<c>ORCT</c>).</summary>
    private const int RemarksMaxLength = 254;

    /// <summary>
    /// Arma el <c>Remarks</c> de un pago. El <c>payment_uuid</c> es obligatorio y
    /// va SIEMPRE de primero.
    /// </summary>
    /// <remarks>
    /// El orden no es cosmético: el campo se recorta a
    /// <see cref="RemarksMaxLength"/> caracteres, así que poner el uuid al final
    /// permitiría que un contexto largo lo borrara justo cuando más se necesita
    /// (auditar un pago que salió mal). De primero, es intocable.
    /// </remarks>
    /// <param name="paymentUuid">El <c>payment_uuid</c> que envía el middleware.</param>
    /// <param name="context">Contexto opcional para un humano que lea el documento en SAP.</param>
    public static string BuildRemarks(string paymentUuid, string? context = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentUuid);

        var remarks = $"payment_uuid={paymentUuid}";

        if (!string.IsNullOrWhiteSpace(context))
        {
            remarks = $"{remarks} | {context}";
        }

        return remarks.Length <= RemarksMaxLength ? remarks : remarks[..RemarksMaxLength];
    }
}

/// <summary>
/// Una aplicación del pago contra una factura. Complex type <c>PaymentInvoice</c>.
/// </summary>
public sealed class IncomingPaymentInvoiceLine
{
    /// <summary>
    /// <b>DocEntry de la factura</b> — la clave interna, no el <c>DocNum</c>.
    /// Es lo único que identifica el documento destino, y la razón de existir de
    /// toda la resolución local por SQL.
    /// </summary>
    [JsonPropertyName("DocEntry")]
    public int DocEntry { get; set; }

    [JsonPropertyName("SumApplied")]
    public decimal SumApplied { get; set; }

    /// <summary>Factura de venta. Enum <c>BoRcptInvTypes</c>.</summary>
    [JsonPropertyName("InvoiceType")]
    public string InvoiceType { get; set; } = "it_Invoice";
}
