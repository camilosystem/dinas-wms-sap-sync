using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// El portón de los campos que el contrato v0.37.2 agregó y este lado todavía no
/// traslada a SAP: <c>invoice_discount_pct</c> y <c>freight_amount</c>.
/// </summary>
/// <remarks>
/// Lo que se fija acá no es una validación más, es el ORDEN: rechazar ANTES de
/// escribir. Sin el portón la factura se crea igual —sin descuento y sin flete,
/// por menos de lo autorizado— y el contraste contra <c>expected_doc_total</c>
/// corre DESPUÉS del POST, así que solo alcanza a dejar una línea de error sobre
/// un documento irreversible que ya existe y una tarea marcada como integrada.
///
/// <para>
/// La sesión se pasa en <c>null!</c> a propósito: si algún día el rechazo dejara
/// de ocurrir antes de tocar Service Layer, estos tests fallarían con
/// <c>NullReferenceException</c> en vez de pasar. Esa es justamente la propiedad
/// que interesa — que no haya ninguna llamada a SAP antes del rechazo.
/// </para>
/// </remarks>
public class OrderInvoiceGateTests
{
    private static OrderInvoiceIntegrator Integrador() =>
        new(
            Options.Create(new InvoicesOptions { WarehouseCode = "01" }),
            NullLogger<OrderInvoiceIntegrator>.Instance);

    /// <summary>Una tarea por lo demás perfectamente integrable.</summary>
    private static SapOrderInvoiceSyncTask TareaSana(
        decimal? descuentoDocumento = null,
        decimal? flete = null) =>
        new()
        {
            TaskId = 77,
            OrderInvoice = new SapOrderInvoiceSnapshot
            {
                OrderUuid = "6f4df862-7a14-497f-87d5-51d28699d072",
                ClientCode = "C100010",
                InvoiceDate = "2026-08-12",
                ExpectedDocTotal = 273.00m,
                InvoiceDiscountPct = descuentoDocumento,
                FreightAmount = flete,
                Lines =
                [
                    new SapOrderInvoiceLine
                    {
                        ItemCode = "GDD1108",
                        Quantity = 6m,
                        UnitPrice = 45.5m,
                        DiscountPct = 0m,
                    },
                ],
            },
        };

    [Fact]
    public async Task ConDescuentoDeDocumento_seRechazaSinTocarSap()
    {
        var resultado = await Integrador()
            .IntegrarAsync(null!, TareaSana(descuentoDocumento: 10m), CancellationToken.None);

        Assert.False(resultado.Integrada);
        Assert.Null(resultado.DocNum);
        Assert.Contains("invoice_discount_pct", resultado.Error);
    }

    [Fact]
    public async Task ConFlete_seRechazaSinTocarSap()
    {
        var resultado = await Integrador()
            .IntegrarAsync(null!, TareaSana(flete: 25.50m), CancellationToken.None);

        Assert.False(resultado.Integrada);
        Assert.Null(resultado.DocNum);
        Assert.Contains("freight_amount", resultado.Error);
    }

    [Fact]
    public async Task ConLosDos_elErrorNombraLosDos()
    {
        // El middleware guarda este texto como error_detail. Que nombre los dos
        // motivos importa: si solo dijera uno, resolver ese campo dejaría la
        // tarea rebotando por el otro sin que nadie supiera por qué.
        var resultado = await Integrador()
            .IntegrarAsync(null!, TareaSana(descuentoDocumento: 10m, flete: 25.50m), CancellationToken.None);

        Assert.False(resultado.Integrada);
        Assert.Contains("invoice_discount_pct", resultado.Error);
        Assert.Contains("freight_amount", resultado.Error);
    }

    /// <remarks>
    /// Al no rechazar, la ejecución sigue hasta la primera llamada a Service
    /// Layer y revienta contra la sesión nula. Esa excepción ES el aserto: prueba
    /// que el portón dejó pasar y que la ruta normal sigue intacta.
    /// </remarks>
    [Fact]
    public async Task SinLosCamposNuevos_elPortonNoSeMete()
    {
        var integrador = Integrador();

        await Assert.ThrowsAsync<NullReferenceException>(() =>
            integrador.IntegrarAsync(null!, TareaSana(), CancellationToken.None));
    }

    [Fact]
    public async Task ConDescuentoYFleteEnCero_elPortonNoSeMete()
    {
        // Cero y ausente son lo mismo: no hay nada que trasladar. Una orden sin
        // descuento ni flete tiene que seguir facturándose como siempre — el
        // portón cierra el paso a lo que no sabemos hacer, no a lo que sí.
        var integrador = Integrador();

        await Assert.ThrowsAsync<NullReferenceException>(() =>
            integrador.IntegrarAsync(null!, TareaSana(0m, 0m), CancellationToken.None));
    }
}
