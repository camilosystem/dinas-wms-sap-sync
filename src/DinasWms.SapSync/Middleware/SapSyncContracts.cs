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
