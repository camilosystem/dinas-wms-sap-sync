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

    /// <summary>
    /// NOMBRE del gasto adicional con el que se cobra el flete, tal como está
    /// definido en la configuración de gastos de la empresa.
    /// </summary>
    /// <remarks>
    /// Se configura el nombre y NO el código: el <c>ExpenseCode</c> es un dato
    /// maestro y se resuelve consultando <c>AdditionalExpenses</c>, por la misma
    /// razón por la que los <c>bin_code</c> se resuelven contra
    /// <c>BinLocations</c> en vez de escribir los <c>AbsEntry</c> a mano. Un
    /// número copiado a un archivo de configuración es correcto hasta que alguien
    /// reordena los datos maestros, y entonces el flete se cobra contra otra
    /// cuenta sin que nada falle.
    ///
    /// <para>
    /// En <c>SUPPORT_DINAS</c> resuelve a <c>ExpensCode 11</c>, y es el único de
    /// los once gastos definidos con <c>RevenuesAccount</c> (4000): los otros diez
    /// son gastos de importación, con <c>DistributionMethod aed_LineTotal</c> y
    /// <c>Stock tYES</c>, que reparten en el costo del inventario. Este va con
    /// <c>aed_None</c> y <c>Stock tNO</c> — se cobra, no se costea.
    /// </para>
    /// </remarks>
    public string FreightExpenseName { get; set; } = "Domestic Freight";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(WarehouseCode))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(WarehouseCode)} no puede estar vacío.");
        }

        if (string.IsNullOrWhiteSpace(FreightExpenseName))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(FreightExpenseName)} no puede estar vacío: sin él no se " +
                "puede resolver contra qué gasto se cobra el flete.");
        }
    }
}
