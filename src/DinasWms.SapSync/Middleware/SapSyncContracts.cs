using System.Text.Json;
using System.Text.Json.Serialization;

namespace DinasWms.SapSync.Middleware;

/// <summary>
/// Contratos de <c>/admin/sap-sync/account-payments/*</c>.
/// </summary>
/// <remarks>
/// Los nombres salen del OpenAPI real del middleware
/// (<c>Dinas.Wms.Api</c>, <c>/swagger/v1/swagger.json</c>), no de suposiciones.
/// Dos de ellos habrían sido mal adivinados: el resultado se reporta con
/// <c>sap_reference</c> (no <c>doc_num</c>) y <c>error_detail</c> (no
/// <c>error_message</c>).
/// </remarks>
public static class SapSyncJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>Respuesta de <c>GET .../pending</c>: un envoltorio con la cola.</summary>
public sealed class SapAccountPaymentSyncTasksPage
{
    [JsonPropertyName("tasks")]
    public List<SapAccountPaymentSyncTask>? Tasks { get; set; }
}

/// <summary>Una tarea de la cola de integración.</summary>
public sealed class SapAccountPaymentSyncTask
{
    [JsonPropertyName("task_id")]
    public int TaskId { get; set; }

    [JsonPropertyName("document_uuid")]
    public string? DocumentUuid { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    /// <summary>La tarea se reactivó a mano, no por el flujo normal.</summary>
    [JsonPropertyName("forced")]
    public bool Forced { get; set; }

    /// <summary>Intentos previos. Útil para no reintentar en vano para siempre.</summary>
    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    [JsonPropertyName("error_detail")]
    public string? ErrorDetail { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("last_attempt_at")]
    public DateTimeOffset? LastAttemptAt { get; set; }

    [JsonPropertyName("account_payment")]
    public SapAccountPaymentSnapshot? AccountPayment { get; set; }
}

/// <summary>El pago de cartera tal como lo aprobó el Dashboard.</summary>
public sealed class SapAccountPaymentSnapshot
{
    [JsonPropertyName("payment_uuid")]
    public string? PaymentUuid { get; set; }

    [JsonPropertyName("client_code")]
    public string? ClientCode { get; set; }

    /// <summary>Total recibido del cliente, que puede ser mayor que lo aplicado.</summary>
    [JsonPropertyName("amount")]
    public decimal Amount { get; set; }

    /// <summary>EFECTIVO, TRANSFERENCIA o CHEQUE.</summary>
    [JsonPropertyName("method")]
    public string? Method { get; set; }

    [JsonPropertyName("payment_channel")]
    public string? PaymentChannel { get; set; }

    [JsonPropertyName("transfer_bank_account")]
    public string? TransferBankAccount { get; set; }

    /// <summary>
    /// Número del cheque. Viaja como texto en el contrato, pero SAP lo exige
    /// entero — la conversión se valida, no se asume.
    /// </summary>
    [JsonPropertyName("check_number")]
    public string? CheckNumber { get; set; }

    [JsonPropertyName("bank_code")]
    public string? BankCode { get; set; }

    /// <summary>
    /// Cuenta contable ya resuelta por el middleware a partir del canal / banco.
    /// Cuando viene, manda sobre la configuración local.
    /// </summary>
    [JsonPropertyName("resolved_account_code")]
    public string? ResolvedAccountCode { get; set; }

    [JsonPropertyName("payment_date")]
    public string? PaymentDate { get; set; }

    [JsonPropertyName("confirmed_applications")]
    public List<InvoiceApplication>? ConfirmedApplications { get; set; }

    [JsonPropertyName("unapplied_amount")]
    public decimal UnappliedAmount { get; set; }

    [JsonPropertyName("decided_by")]
    public string? DecidedBy { get; set; }

    [JsonPropertyName("decided_at")]
    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary>Una aplicación del pago contra una factura, ya confirmada.</summary>
public sealed class InvoiceApplication
{
    /// <summary>
    /// <c>DocNum</c> de la factura como texto. El <c>DocEntry</c> que necesita
    /// Service Layer se resuelve localmente por SQL — nunca viaja por el contrato.
    /// </summary>
    [JsonPropertyName("invoice_doc_num")]
    public string? InvoiceDocNum { get; set; }

    [JsonPropertyName("amount")]
    public decimal? Amount { get; set; }
}

/// <summary>Respuesta de <c>GET /admin/sap-sync/order-invoices/pending</c>.</summary>
/// <remarks>
/// El sobre de la tarea es idéntico al de los pagos; lo único que cambia es el
/// snapshot que cuelga (<c>order_invoice</c> en vez de <c>account_payment</c>).
/// Confirmado contra la respuesta real del middleware (tarea 15, 31-jul-2026).
/// </remarks>
public sealed class SapOrderInvoiceSyncTasksPage
{
    [JsonPropertyName("tasks")]
    public List<SapOrderInvoiceSyncTask>? Tasks { get; set; }
}

/// <summary>Una tarea de la cola de facturas de órdenes ya picadas.</summary>
public sealed class SapOrderInvoiceSyncTask
{
    [JsonPropertyName("task_id")]
    public int TaskId { get; set; }

    [JsonPropertyName("document_uuid")]
    public string? DocumentUuid { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("forced")]
    public bool Forced { get; set; }

    [JsonPropertyName("attempts")]
    public int Attempts { get; set; }

    [JsonPropertyName("error_detail")]
    public string? ErrorDetail { get; set; }

    [JsonPropertyName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [JsonPropertyName("last_attempt_at")]
    public DateTimeOffset? LastAttemptAt { get; set; }

    [JsonPropertyName("order_invoice")]
    public SapOrderInvoiceSnapshot? OrderInvoice { get; set; }
}

/// <summary>
/// La factura a crear, tal como quedó al completarse el picking.
/// </summary>
/// <remarks>
/// Es una factura STANDALONE: no hay Orden de Venta en SAP que le sirva de base.
/// Las cantidades ya vienen resueltas desde el <c>PalletAssignment</c> (lo
/// realmente picado, no lo pedido) y el precio ya viene resuelto y CONGELADO
/// desde la <c>OrderLine</c>. El WMS no los revalida y SAP no los recalcula.
///
/// Lo que este contrato NO trae, y hay que resolver por fuera:
///   · el ALMACÉN del que sale la mercancía (hoy: configuración, 01);
///   · la UNIDAD de <c>quantity</c> (SAP factura en unidad de venta).
/// </remarks>
public sealed class SapOrderInvoiceSnapshot
{
    /// <summary>
    /// Identidad de la orden. En la cola real llega con el mismo valor que
    /// <c>document_uuid</c>: la factura no tiene uuid propio, se rastrea por la
    /// orden que la originó. Es lo que va en <c>Comments</c>.
    /// </summary>
    [JsonPropertyName("order_uuid")]
    public string? OrderUuid { get; set; }

    [JsonPropertyName("client_code")]
    public string? ClientCode { get; set; }

    /// <summary>Camión del despacho. No le sirve a SAP; sí como contexto humano.</summary>
    [JsonPropertyName("truck_id")]
    public string? TruckId { get; set; }

    /// <summary>Fecha de la factura, <c>yyyy-MM-dd</c>.</summary>
    [JsonPropertyName("invoice_date")]
    public string? InvoiceDate { get; set; }

    [JsonPropertyName("lines")]
    public List<SapOrderInvoiceLine>? Lines { get; set; }

    /// <summary>
    /// Total que el WMS espera de esta factura. Es la única referencia
    /// autoritativa contra la cual contrastar el <c>DocTotal</c> que devuelve
    /// SAP: si difieren, el documento no dice lo que el WMS autorizó, y eso se
    /// reporta — no se corrige por cuenta propia.
    /// </summary>
    [JsonPropertyName("expected_doc_total")]
    public decimal? ExpectedDocTotal { get; set; }
}

/// <summary>Una línea de la factura, ya picada y con el precio congelado.</summary>
public sealed class SapOrderInvoiceLine
{
    [JsonPropertyName("item_code")]
    public string? ItemCode { get; set; }

    /// <summary>Lo realmente picado (<c>PalletAssignment</c>), no lo pedido.</summary>
    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; set; }

    /// <summary>Precio ANTES de descuento, congelado por el WMS.</summary>
    [JsonPropertyName("unit_price")]
    public decimal? UnitPrice { get; set; }

    /// <summary>Descuento de la línea. 100 es válido: es una promoción.</summary>
    [JsonPropertyName("discount_pct")]
    public decimal? DiscountPct { get; set; }

    /// <summary>Total de la línea según el WMS, ya con el descuento aplicado.</summary>
    [JsonPropertyName("expected_line_total")]
    public decimal? ExpectedLineTotal { get; set; }

    /// <summary>
    /// De qué ubicaciones sale la mercancía de esta línea. El reparto lo hace el
    /// middleware; acá se usa TAL CUAL, sin recalcular.
    /// </summary>
    [JsonPropertyName("bin_allocations")]
    public List<SapOrderInvoiceBinAllocation>? BinAllocations { get; set; }
}

/// <summary>Una asignación de bin de una línea.</summary>
/// <remarks>
/// Viene con el <c>bin_code</c> (el que lee un humano), pero SAP exige el
/// <c>AbsEntry</c> del bin. Ese mapeo se resuelve localmente contra la entidad
/// <c>BinLocations</c>, por la misma razón y con el mismo criterio que el
/// <c>DocEntry</c> de las facturas: las claves internas de SAP no viajan por el
/// contrato.
/// </remarks>
public sealed class SapOrderInvoiceBinAllocation
{
    [JsonPropertyName("bin_code")]
    public string? BinCode { get; set; }

    [JsonPropertyName("quantity")]
    public decimal? Quantity { get; set; }

    /// <summary>
    /// <c>true</c> = el WMS NO sabe de qué bin salió realmente la mercancía y
    /// repartió por saldo disponible. El documento de SAP va a afirmar una salida
    /// por ubicación que puede no ser la física. Se registra siempre.
    /// </summary>
    [JsonPropertyName("approximate")]
    public bool Approximate { get; set; }
}

/// <summary>Cuerpo de <c>POST .../{taskId}/result</c>.</summary>
public sealed class SapSyncResultReport
{
    public const string StatusIntegrado = "INTEGRADO";
    public const string StatusError = "ERROR";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    /// <summary>
    /// Referencia del documento creado en SAP. Se envía el <c>DocNum</c> del
    /// pago, que es el número que un humano puede buscar en Business One.
    /// </summary>
    [JsonPropertyName("sap_reference")]
    public string? SapReference { get; set; }

    [JsonPropertyName("error_detail")]
    public string? ErrorDetail { get; set; }

    public static SapSyncResultReport Integrado(int docNum) =>
        new() { Status = StatusIntegrado, SapReference = docNum.ToString() };

    public static SapSyncResultReport Error(string detalle) =>
        new()
        {
            Status = StatusError,
            // El middleware guarda esto para que alguien lo lea y decida. Se
            // recorta para no mandar un muro de texto, pero lo suficiente para
            // diagnosticar.
            ErrorDetail = detalle.Length <= 1000 ? detalle : detalle[..1000],
        };

    public string ToJson() => JsonSerializer.Serialize(this, SapSyncJson.Options);
}
