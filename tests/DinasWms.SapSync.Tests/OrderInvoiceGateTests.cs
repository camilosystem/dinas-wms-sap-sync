using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// El portón aritmético: rehacer las cuentas del documento y contrastarlas con
/// <c>expected_doc_total</c> ANTES de escribir.
/// </summary>
/// <remarks>
/// Reemplazó a un portón que nombraba campos sueltos (<c>invoice_discount_pct</c>,
/// <c>freight_amount</c>), y es estrictamente más fuerte: no defiende contra dos
/// campos, defiende contra la clase entera. Cualquier campo futuro que afecte al
/// dinero y que el integrador ignore hace que el total calculado difiera del que
/// espera el contrato, y la escritura se rechaza sola — sin que nadie tenga que
/// acordarse de agregar una validación cuando el contrato crezca.
///
/// <para>
/// Es también la razón por la que <c>SapSyncJson</c> NO usa
/// <c>UnmappedMemberHandling.Disallow</c>: eso convertiría cada campo aditivo del
/// contrato —que se supone seguro— en una caída de la facturación. El chequeo
/// aritmético cubre lo que importa sin romper con lo que no.
/// </para>
/// <para>
/// La sesión va en <c>null!</c> a propósito: si el rechazo dejara de ocurrir antes
/// de tocar Service Layer, estos tests fallarían con <c>NullReferenceException</c>
/// en vez de pasar. Esa es la propiedad que interesa — que no haya ninguna
/// llamada a SAP antes del rechazo.
/// </para>
/// </remarks>
public class OrderInvoiceGateTests
{
    private static OrderInvoiceIntegrator Integrador() =>
        new(
            Options.Create(new InvoicesOptions { WarehouseCode = "01" }),
            NullLogger<OrderInvoiceIntegrator>.Instance);

    /// <summary>
    /// Una línea de 6 x 45.50 sin descuento: 273.00 exactos. Sin asignaciones de
    /// bin, para que no haga falta sesión hasta el anti-duplicado.
    /// </summary>
    private static SapOrderInvoiceSyncTask Tarea(
        decimal? esperadoDocumento,
        decimal? descuentoDocumento = null,
        decimal? flete = null,
        decimal? esperadoLinea = null) =>
        new()
        {
            TaskId = 77,
            OrderInvoice = new SapOrderInvoiceSnapshot
            {
                OrderUuid = "6f4df862-7a14-497f-87d5-51d28699d072",
                ClientCode = "C100010",
                InvoiceDate = "2026-08-12",
                ExpectedDocTotal = esperadoDocumento,
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
                        ExpectedLineTotal = esperadoLinea,
                    },
                ],
            },
        };

    [Fact]
    public async Task SiElTotalNoCuadra_seRechazaSinTocarSap()
    {
        // El caso general, y el que importa: el WMS contó 300.00 y las líneas dan
        // 273.00. Algo que el WMS sí contó no está llegando a la factura. Da
        // igual qué campo sea — incluso uno que todavía no exista.
        var resultado = await Integrador()
            .IntegrarAsync(null!, Tarea(esperadoDocumento: 300.00m), CancellationToken.None);

        Assert.False(resultado.Integrada);
        Assert.Null(resultado.DocNum);
        Assert.Contains("expected_doc_total", resultado.Error);
    }

    [Fact]
    public async Task DescuentoDeDocumentoQueElContratoNoContemplo_seRechaza()
    {
        // 273.00 con 10% da 245.70. Si el contrato dice 273.00, el descuento no
        // está en el total esperado y alguien está contando distinto.
        var resultado = await Integrador()
            .IntegrarAsync(
                null!,
                Tarea(esperadoDocumento: 273.00m, descuentoDocumento: 10m),
                CancellationToken.None);

        Assert.False(resultado.Integrada);
        Assert.Contains("245.70", resultado.Error);
    }

    [Fact]
    public async Task LineaCuyoExpectedLineTotalNoCuadra_seRechaza()
    {
        // Con una entrada por línea del pedido y el mismo item_code repetido a
        // precios distintos, un reparto mal hecho se ve acá — antes de escribir.
        var resultado = await Integrador()
            .IntegrarAsync(
                null!,
                Tarea(esperadoDocumento: 273.00m, esperadoLinea: 250.00m),
                CancellationToken.None);

        Assert.False(resultado.Integrada);
        Assert.Contains("expected_line_total", resultado.Error);
    }

    [Fact]
    public async Task ConDescuentoYTotalCoherente_elPortonDejaPasar()
    {
        // 273.00 − 27.30 = 245.70. Cuadra, así que la ejecución sigue hasta el
        // anti-duplicado y revienta contra la sesión nula. Esa excepción ES el
        // aserto: el portón cierra el paso a lo que no cuadra, no a lo que sí.
        var integrador = Integrador();

        await Assert.ThrowsAsync<NullReferenceException>(() =>
            integrador.IntegrarAsync(
                null!,
                Tarea(esperadoDocumento: 245.70m, descuentoDocumento: 10m),
                CancellationToken.None));
    }

    [Fact]
    public async Task SinCamposNuevosYTotalCoherente_laRutaNormalSigueIntacta()
    {
        var integrador = Integrador();

        await Assert.ThrowsAsync<NullReferenceException>(() =>
            integrador.IntegrarAsync(
                null!,
                Tarea(esperadoDocumento: 273.00m, esperadoLinea: 273.00m),
                CancellationToken.None));
    }
}
