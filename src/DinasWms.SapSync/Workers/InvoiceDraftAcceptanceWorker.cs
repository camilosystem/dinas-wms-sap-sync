using System.Globalization;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Criterio de aceptación del contrato v0.37.2: arma una tarea sintética con
/// descuento de documento y flete, la pasa por el integrador REAL en modo
/// borrador, y contrasta el <c>DocTotal</c> que calcula SAP contra el
/// <c>expected_doc_total</c> del contrato.
/// </summary>
/// <remarks>
/// No asienta nada: va a <c>/Drafts</c>, se relee en la respuesta y se borra.
///
/// <para>
/// <b>Los números son feos a propósito.</b> Con un descuento y un flete
/// "lindos", varios órdenes de aplicación dan el mismo resultado y el verde no
/// prueba nada. Los valores por defecto están elegidos para que el redondeo
/// quede a la vista:
/// </para>
///
/// <list type="bullet">
/// <item>7 x 33.33 con 3.5% da 225.14415 — se redondea a 225.14, y el descartado
/// no es cero.</item>
/// <item>El descuento de documento cae en 23.8970550, que redondea a 23.90 hacia
/// arriba. Si SAP truncara o redondeara al par, daría 23.89 y la diferencia
/// aparecería.</item>
/// <item>El flete, 12.37, se suma SIN descontar: si algún día se descontara, el
/// total se movería 0.91.</item>
/// </list>
///
/// <para>
/// Además el MISMO <c>item_code</c> va en dos entradas a precios distintos, que
/// es la forma de la promoción que motivó el cambio de contrato. La respuesta
/// literal del borrador muestra qué <c>LineNum</c> les asignó SAP, que es de lo
/// que cuelgan las asignaciones de bin.
/// </para>
///
/// Uso:
///   --RunMode=InvoiceDraftAcceptance --Probe:Confirm=true
///   [--Probe:CardCode=C100010] [--Probe:ItemCode=GDD1108]
/// </remarks>
public sealed class InvoiceDraftAcceptanceWorker : BackgroundService
{
    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly OrderInvoiceIntegrator _integrador;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<InvoiceDraftAcceptanceWorker> _logger;

    public InvoiceDraftAcceptanceWorker(
        IServiceLayerSessionFactory sessionFactory,
        OrderInvoiceIntegrator integrador,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<InvoiceDraftAcceptanceWorker> logger)
    {
        _sessionFactory = sessionFactory;
        _integrador = integrador;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();

        try
        {
            await CorrerAsync(stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Apagado normal.
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
            _logger.LogError(ex, "ENSAYO DE ACEPTACIÓN FALLIDO. {Message}", ex.Message);
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task CorrerAsync(CancellationToken cancellationToken)
    {
        var cliente = _configuration["Probe:CardCode"] ?? "C100010";
        var item = _configuration["Probe:ItemCode"] ?? "GDD1108";

        // Bins OPCIONALES, uno DISTINTO por línea. Es el último eslabón sin
        // verificar de la cadena que escribe: las asignaciones se enganchan a la
        // línea por BaseLineNumber, y con el mismo item_code en dos entradas, si
        // SAP las mezclara, la mercancía saldría del bin equivocado. No es un
        // error de plata: es de inventario físico, y esos se descubren contando.
        // Dos bins distintos hacen que una mezcla sea visible; con el mismo bin
        // en las dos líneas, un cruce pasaría desapercibido.
        var bin1 = _configuration["Probe:BinCode1"];
        var bin2 = _configuration["Probe:BinCode2"];

        // --- Los números, calculados acá con la misma aritmética del contrato --
        // Se calculan y NO se copian: si el integrador y este arnés difirieran,
        // el portón aritmético rechazaría antes de llegar a SAP, que también es
        // información.
        const decimal precio = 33.33m;
        const decimal descuentoLinea = 3.5m;
        const decimal descuentoDocumento = 7.35m;
        const decimal flete = 12.37m;

        var linea1 = Redondear(7m * precio * (1m - descuentoLinea / 100m));
        var linea2 = Redondear(3m * precio);
        var subtotal = linea1 + linea2;
        var descuento = Redondear(subtotal * descuentoDocumento / 100m);
        var total = Redondear(subtotal - descuento + flete);

        _logger.LogInformation(
            "=== ENSAYO DE ACEPTACIÓN — números elegidos para que el redondeo se vea ===\n" +
            "  línea 1: 7 x {Precio} con {DescLinea}% = {Bruto} → {Linea1}\n" +
            "  línea 2: 3 x {Precio2} sin descuento   = {Linea2}   (MISMO item_code, a propósito)\n" +
            "  subtotal                               = {Subtotal}\n" +
            "  descuento de documento {DescDoc}%      = {Crudo} → {Descuento}\n" +
            "  flete (se suma sin descontar)          = {Flete}\n" +
            "  expected_doc_total                     = {Total}",
            precio,
            descuentoLinea,
            (7m * precio * (1m - descuentoLinea / 100m)).ToString(CultureInfo.InvariantCulture),
            linea1,
            precio,
            linea2,
            subtotal,
            descuentoDocumento,
            (subtotal * descuentoDocumento / 100m).ToString(CultureInfo.InvariantCulture),
            descuento,
            flete,
            total);

        if (!Bandera("Probe:Confirm"))
        {
            _logger.LogWarning(
                "SIMULACIÓN — no se envía nada. El borrador NO asienta contabilidad ni mueve " +
                "inventario y se borra al final, pero igual escribe en SAP. Hace falta " +
                "--Probe:Confirm=true.");
            return;
        }

        var tarea = new SapOrderInvoiceSyncTask
        {
            TaskId = 0,
            OrderInvoice = new SapOrderInvoiceSnapshot
            {
                // Uuid propio del ensayo: no puede chocar con el anti-duplicado
                // de ninguna orden real.
                OrderUuid = $"ensayo-aceptacion-v0372-{item}",
                ClientCode = cliente,
                TruckId = "ENSAYO",
                InvoiceDate = DateTime.Today.ToString("yyyy-MM-dd"),
                InvoiceDiscountPct = descuentoDocumento,
                FreightAmount = flete,
                ExpectedDocTotal = total,
                Lines =
                [
                    new SapOrderInvoiceLine
                    {
                        ItemCode = item,
                        Quantity = 7m,
                        UnitPrice = precio,
                        DiscountPct = descuentoLinea,
                        ExpectedLineTotal = linea1,
                        BinAllocations = Bins(bin1, 7m),
                    },
                    new SapOrderInvoiceLine
                    {
                        ItemCode = item,
                        Quantity = 3m,
                        UnitPrice = precio,
                        DiscountPct = 0m,
                        ExpectedLineTotal = linea2,
                        BinAllocations = Bins(bin2, 3m),
                    },
                ],
            },
        };

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var resultado = await _integrador
            .IntegrarAsync(session, tarea, cancellationToken, soloBorrador: true)
            .ConfigureAwait(false);

        if (resultado.Error is not null)
        {
            Environment.ExitCode = 1;
            _logger.LogError("El integrador rechazó el ensayo: {Error}", resultado.Error);
            return;
        }

        if (resultado.Advertencia is not null)
        {
            Environment.ExitCode = 1;
            _logger.LogError("{Advertencia}", resultado.Advertencia);
        }

        // --- El veredicto ------------------------------------------------------
        if (resultado.DocTotal is null)
        {
            Environment.ExitCode = 1;
            _logger.LogError("No se pudo leer el DocTotal del borrador: el ensayo no concluye.");
            return;
        }

        if (resultado.DocTotal.Value == total)
        {
            _logger.LogInformation(
                "=== ACEPTADO. SAP calculó {Real} y el contrato esperaba {Esperado}: la aritmética " +
                "de este lado coincide con la de SAP, redondeo incluido. ===",
                resultado.DocTotal.Value.ToString("F2", CultureInfo.InvariantCulture),
                total.ToString("F2", CultureInfo.InvariantCulture));
            return;
        }

        Environment.ExitCode = 1;
        _logger.LogError(
            "=== NO ACEPTADO. SAP calculó {Real} y este lado esperaba {Esperado}, diferencia de " +
            "{Dif}. NO levantar el portón: con esta diferencia, facturar de verdad crearía " +
            "documentos por un monto distinto del autorizado. ===",
            resultado.DocTotal.Value.ToString("F2", CultureInfo.InvariantCulture),
            total.ToString("F2", CultureInfo.InvariantCulture),
            (resultado.DocTotal.Value - total).ToString("F2", CultureInfo.InvariantCulture));
    }

    private static List<SapOrderInvoiceBinAllocation>? Bins(string? binCode, decimal cantidad) =>
        string.IsNullOrWhiteSpace(binCode)
            ? null
            : [new SapOrderInvoiceBinAllocation { BinCode = binCode, Quantity = cantidad }];

    private static decimal Redondear(decimal v) =>
        decimal.Round(v, 2, MidpointRounding.AwayFromZero);

    private bool Bandera(string clave) =>
        string.Equals(_configuration[clave], "true", StringComparison.OrdinalIgnoreCase);
}
