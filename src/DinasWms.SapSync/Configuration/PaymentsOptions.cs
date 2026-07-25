namespace DinasWms.SapSync.Configuration;

/// <summary>
/// Cuentas contables por medio de pago.
/// </summary>
/// <remarks>
/// Estas cuentas son una decisión contable, no técnica: en SUPPORT_DINAS hay más
/// de una cuenta en uso por método, y elegir la equivocada registra el dinero en
/// el lugar equivocado. Por eso viven en configuración y no hay valores por
/// defecto inventados — si falta la del método que llega, se falla en vez de
/// adivinar.
///
/// Provisional por diseño: el middleware va a expandir su contrato para que el
/// usuario elija la cuenta específica al registrar el pago. Cuando eso exista,
/// la cuenta llegará en el payload y esta configuración pasará a ser solo el
/// respaldo (o desaparece).
/// </remarks>
public sealed class PaymentsOptions
{
    public const string SectionName = "Payments";

    /// <summary>Cuenta para pagos en efectivo. Ej. 110002 (General Cash).</summary>
    public string? CashAccount { get; set; }

    /// <summary>Cuenta para transferencias. Pendiente de decisión.</summary>
    public string? TransferAccount { get; set; }

    /// <summary>
    /// Cuenta de control para cheques. Pendiente de decisión, y además CHEQUE no
    /// es implementable hasta que el contrato traiga número de cheque y banco.
    /// </summary>
    public string? CheckAccount { get; set; }

    /// <summary>
    /// Referencia que SAP guarda en las transferencias. Los pagos reales de esta
    /// base usan "ACH".
    /// </summary>
    public string TransferReference { get; set; } = "ACH";

    /// <summary>
    /// Devuelve la cuenta del método indicado, o lanza si no está configurada.
    /// </summary>
    public string RequireAccountFor(string metodo)
    {
        var (cuenta, nombreOpcion) = metodo.ToUpperInvariant() switch
        {
            "EFECTIVO" => (CashAccount, nameof(CashAccount)),
            "TRANSFERENCIA" => (TransferAccount, nameof(TransferAccount)),
            "CHEQUE" => (CheckAccount, nameof(CheckAccount)),
            _ => throw new InvalidOperationException(
                $"Método de pago no reconocido: '{metodo}'. Esperados: EFECTIVO, TRANSFERENCIA, CHEQUE."),
        };

        if (string.IsNullOrWhiteSpace(cuenta))
        {
            throw new InvalidOperationException(
                $"No hay cuenta configurada para el método '{metodo}'. " +
                $"Configurar {SectionName}:{nombreOpcion}. No se elige una por defecto: " +
                "registrar el dinero en la cuenta equivocada es peor que no registrarlo.");
        }

        return cuenta;
    }
}
