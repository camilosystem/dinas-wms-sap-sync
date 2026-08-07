namespace DinasWms.SapSync.Configuration;

/// <summary>
/// Configuración de la integración de facturas de órdenes ya picadas.
/// </summary>
/// <remarks>
/// El almacén vive acá y no en el contrato porque el snapshot no lo trae: el
/// middleware no dice de dónde sale la mercancía. Hoy el WMS opera un solo
/// almacén (01, JAMAICA) y ponerlo en configuración es honesto — es una decisión
/// de este lado, no un dato del documento. El día que el WMS opere un segundo
/// almacén, esto tiene que pasar al contrato: si no, las facturas del segundo
/// almacén saldrían del primero en silencio.
/// </remarks>
public sealed class InvoicesOptions
{
    public const string SectionName = "Invoices";

    /// <summary>Almacén del que sale la mercancía facturada.</summary>
    public string WarehouseCode { get; set; } = "01";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WarehouseCode))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(WarehouseCode)} no puede estar vacío.");
        }
    }
}
