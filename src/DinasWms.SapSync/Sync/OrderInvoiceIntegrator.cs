using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.ServiceLayer.Invoices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Sync;

/// <summary>
/// Convierte UNA tarea de <c>order-invoices</c> en una factura de SAP.
/// </summary>
/// <remarks>
/// Existe para que el paso automático (<see cref="OrderInvoicesSyncStep"/>) y el
/// arnés manual (<c>--RunMode=InvoiceProbe</c>) compartan exactamente la misma
/// lógica. Duplicarla sería la peor clase de deuda: el anti-duplicado, la
/// resolución de bins y la validación de montos son justo lo que no puede
/// divergir entre "como lo probamos" y "como corre solo".
///
/// No reporta al middleware ni abre sesión: las dos cosas son del llamador. El
/// arnés a veces no debe reportar (una prueba no decide el estado de una tarea),
/// y la sesión la administra el ciclo.
/// </remarks>
public sealed class OrderInvoiceIntegrator
{
    private readonly InvoicesOptions _options;
    private readonly ILogger<OrderInvoiceIntegrator> _logger;

    public OrderInvoiceIntegrator(
        IOptions<InvoicesOptions> options,
        ILogger<OrderInvoiceIntegrator> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <param name="simular">
    /// Si es true hace todo menos el POST: valida, resuelve bins, corre el
    /// anti-duplicado y muestra el payload. Es lo que usa el arnés manual para
    /// mirar qué se enviaría sin escribir en SAP.
    /// </param>
    /// <param name="soloBorrador">
    /// Manda el MISMO payload a <c>/Drafts</c> en vez de <c>/Invoices</c>, lo
    /// verifica y lo borra. Es el ensayo que permite contrastar el
    /// <c>DocTotal</c> que calcula SAP contra el <c>expected_doc_total</c> del
    /// contrato sin asentar una factura irreversible.
    /// </param>
    public async Task<OrderInvoiceOutcome> IntegrarAsync(
        ServiceLayerSession session,
        SapOrderInvoiceSyncTask tarea,
        CancellationToken cancellationToken,
        bool simular = false,
        bool soloBorrador = false)
    {
        var snapshot = tarea.OrderInvoice;

        if (snapshot is null)
        {
            return OrderInvoiceOutcome.Rechazada("la tarea no trae order_invoice");
        }

        var lineas = snapshot.Lines ?? [];
        var almacen = _options.WarehouseCode;

        var problemas = Validar(snapshot, lineas);

        if (problemas.Count > 0)
        {
            return OrderInvoiceOutcome.Rechazada(string.Join("; ", problemas));
        }

        // --- Asignaciones de bin ---------------------------------------------
        var codigosBin = lineas
            .SelectMany(l => l.BinAllocations ?? [])
            .Select(b => b.BinCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct()
            .ToList();

        var binPorCodigo = codigosBin.Count == 0
            ? []
            : await ResolverBinsAsync(session, codigosBin, almacen, cancellationToken)
                .ConfigureAwait(false);

        var sinResolver = codigosBin.Where(c => !binPorCodigo.ContainsKey(c)).ToList();

        if (sinResolver.Count > 0)
        {
            return OrderInvoiceOutcome.Rechazada(
                $"bin_code inexistente(s) en el almacén {almacen}: {string.Join(", ", sinResolver)}");
        }

        var descuadres = new List<string>();
        var aproximadas = 0;

        for (var i = 0; i < lineas.Count; i++)
        {
            var asignaciones = lineas[i].BinAllocations ?? [];

            if (asignaciones.Count == 0)
            {
                continue;
            }

            if (asignaciones.Any(b => b.Quantity is null or <= 0))
            {
                descuadres.Add($"línea {i}: alguna asignación de bin viene sin cantidad");
                continue;
            }

            var suma = asignaciones.Sum(b => b.Quantity!.Value);

            if (suma != lineas[i].Quantity!.Value)
            {
                descuadres.Add(
                    $"línea {i} ({lineas[i].ItemCode}): los bins suman {suma} y la línea factura " +
                    $"{lineas[i].Quantity}");
            }

            aproximadas += asignaciones.Count(b => b.Approximate);
        }

        if (descuadres.Count > 0)
        {
            return OrderInvoiceOutcome.Rechazada(
                "el reparto por bins no cuadra con lo facturado: " + string.Join("; ", descuadres));
        }

        if (aproximadas > 0)
        {
            _logger.LogWarning(
                "Tarea {TaskId}: {Cuantas} asignación(es) de bin marcadas approximate=true. SAP va a " +
                "registrar una salida por ubicación que puede no ser la física.",
                tarea.TaskId,
                aproximadas);
        }

        // --- Flete: resolver el gasto por NOMBRE ------------------------------
        // Igual que los bin_code contra BinLocations: el ExpenseCode es un dato
        // maestro y sale de una consulta, no de un número escrito en el código.
        int? codigoFlete = null;

        if (snapshot.FreightAmount is > 0)
        {
            // Sin código de impuesto no se factura el flete. SAP lo exige
            // (-5002) y la definición del gasto no lo trae, así que si nadie lo
            // configuró es que nadie lo decidió — y no se decide acá.
            if (string.IsNullOrWhiteSpace(_options.FreightTaxCode))
            {
                return OrderInvoiceOutcome.Rechazada(
                    $"la orden trae freight_amount={snapshot.FreightAmount} pero " +
                    $"{InvoicesOptions.SectionName}:{nameof(InvoicesOptions.FreightTaxCode)} está " +
                    "vacío; cómo tributa el flete es una decisión de la empresa y no se elige por " +
                    "omisión");
            }

            codigoFlete = await ResolverGastoAsync(
                session, _options.FreightExpenseName, cancellationToken).ConfigureAwait(false);

            if (codigoFlete is null)
            {
                return OrderInvoiceOutcome.Rechazada(
                    $"la orden trae freight_amount={snapshot.FreightAmount} pero el gasto " +
                    $"'{_options.FreightExpenseName}' no está definido en los datos maestros de la " +
                    "empresa; sin él no hay contra qué cobrar el flete");
            }
        }

        // --- El total, ANTES de escribir --------------------------------------
        // Esta es la defensa estructural, y no es solo el chequeo del descuento y
        // del flete: es la defensa contra la clase entera. Cualquier campo futuro
        // que afecte al dinero y que este integrador ignore hace que el total
        // calculado acá difiera de expected_doc_total, y la escritura se rechaza
        // sola — sin necesidad de que nadie se acuerde de agregar una validación.
        //
        // Por eso NO se usa UnmappedMemberHandling.Disallow: eso convertiría cada
        // campo aditivo del contrato, que se supone seguro, en una caída de la
        // facturación. Este chequeo cubre lo que importa sin romper con lo que no.
        var descuadre = VerificarTotalEsperado(snapshot, lineas);

        if (descuadre is not null)
        {
            return OrderInvoiceOutcome.Rechazada(descuadre);
        }

        // --- Anti-duplicado ---------------------------------------------------
        var yaExiste = await BuscarFacturaExistenteAsync(session, snapshot.OrderUuid!, cancellationToken)
            .ConfigureAwait(false);

        if (yaExiste is not null)
        {
            _logger.LogWarning(
                "Tarea {TaskId}: la factura del order_uuid {Uuid} YA existe en SAP (DocNum {DocNum}) " +
                "y no está anulada. No se crea otra.",
                tarea.TaskId,
                snapshot.OrderUuid,
                yaExiste);

            return OrderInvoiceOutcome.YaExistia(yaExiste.Value);
        }

        // --- Payload y POST ---------------------------------------------------
        var payload = ArmarPayload(
            snapshot, lineas, almacen, binPorCodigo, codigoFlete, _options.FreightTaxCode, soloBorrador);
        var json = payload.ToJson();

        if (simular)
        {
            _logger.LogInformation(
                "Tarea {TaskId} — SIMULACIÓN, no se envía nada. Payload que se enviaría:\n{Json}",
                tarea.TaskId,
                json);
            return OrderInvoiceOutcome.SimuladaOk();
        }

        var ruta = soloBorrador ? "Drafts" : "Invoices";

        _logger.LogInformation(
            "Tarea {TaskId}: enviando a SAP (POST /{Ruta})\n{Json}", tarea.TaskId, ruta, json);

        var (status, body) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Post, ruta)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (status is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            return OrderInvoiceOutcome.Rechazada($"SAP rechazó la factura ({(int)status}). {body}");
        }

        var docNum = LeerEntero(body, "DocNum");
        var docTotal = LeerDecimal(body, "DocTotal");

        // --- Ensayo en borrador: contrastar y borrar ---------------------------
        if (soloBorrador)
        {
            var docEntry = LeerEntero(body, "DocEntry");

            // La respuesta literal ES el documento que SAP guardó: trae los
            // LineNum que asignó y las asignaciones de bin como quedaron. Es lo
            // único que se puede mirar, porque enseguida se borra.
            _logger.LogInformation(
                "=== RESPUESTA LITERAL DEL BORRADOR ({Codigo}), {Bytes:N0} bytes ===\n{Body}",
                (int)status,
                body.Length,
                body);

            _logger.LogInformation(
                "ENSAYO: SAP calculó DocTotal {Real} y el contrato esperaba {Esperado} → {Veredicto}",
                docTotal?.ToString("F2", CultureInfo.InvariantCulture) ?? "(ilegible)",
                snapshot.ExpectedDocTotal?.ToString("F2", CultureInfo.InvariantCulture) ?? "(sin dato)",
                docTotal is not null && docTotal == snapshot.ExpectedDocTotal ? "COINCIDE" : "NO COINCIDE");

            var aviso = docEntry is null
                ? "No se pudo leer el DocEntry del borrador: HAY QUE BORRARLO A MANO."
                : await BorrarBorradorAsync(session, docEntry.Value, cancellationToken)
                    .ConfigureAwait(false);

            return OrderInvoiceOutcome.EnsayoTerminado(docEntry, docNum, docTotal, aviso);
        }

        if (docNum is null)
        {
            // SAP aceptó pero no se puede leer el DocNum: la factura EXISTE. No es
            // un error reportable — reportar ERROR sería mentir y un reintento la
            // duplicaría. Se deja que el anti-duplicado del próximo ciclo la
            // encuentre.
            _logger.LogError(
                "Tarea {TaskId}: SAP respondió {Codigo} pero no se pudo leer el DocNum. La factura " +
                "existe. No se reporta nada; el anti-duplicado del próximo ciclo lo resuelve. " +
                "Respuesta: {Body}",
                tarea.TaskId,
                (int)status,
                body);

            return OrderInvoiceOutcome.CreadaSinNumero();
        }

        // El total esperado del contrato es la referencia. Si difiere, la factura
        // ya existe con el total de SAP: se reporta, no se corrige sola.
        if (snapshot.ExpectedDocTotal is not null && docTotal is not null &&
            docTotal.Value != snapshot.ExpectedDocTotal.Value)
        {
            _logger.LogError(
                "Tarea {TaskId}: DocTotal de SAP {Real} != expected_doc_total {Esperado}. La factura " +
                "{DocNum} YA está creada con el total de SAP.",
                tarea.TaskId,
                docTotal.Value.ToString("F2", CultureInfo.InvariantCulture),
                snapshot.ExpectedDocTotal.Value.ToString("F2", CultureInfo.InvariantCulture),
                docNum);
        }

        return OrderInvoiceOutcome.Creada(docNum.Value);
    }

    private List<string> Validar(SapOrderInvoiceSnapshot snapshot, List<SapOrderInvoiceLine> lineas)
    {
        var problemas = new List<string>();

        if (snapshot.InvoiceDiscountPct is < 0 or > 100)
        {
            problemas.Add(
                $"invoice_discount_pct={snapshot.InvoiceDiscountPct} fuera de rango (0 a 100)");
        }

        if (snapshot.FreightAmount is < 0)
        {
            problemas.Add($"freight_amount={snapshot.FreightAmount} es negativo");
        }

        if (string.IsNullOrWhiteSpace(snapshot.OrderUuid))
        {
            problemas.Add("sin order_uuid, y sin él no hay anti-duplicado");
        }

        if (string.IsNullOrWhiteSpace(snapshot.ClientCode))
        {
            problemas.Add("sin client_code");
        }

        if (string.IsNullOrWhiteSpace(snapshot.InvoiceDate))
        {
            problemas.Add("sin invoice_date");
        }

        if (lineas.Count == 0)
        {
            problemas.Add("sin líneas");
        }

        for (var i = 0; i < lineas.Count; i++)
        {
            var l = lineas[i];

            if (string.IsNullOrWhiteSpace(l.ItemCode) ||
                l.Quantity is null or <= 0 ||
                l.UnitPrice is null or < 0 ||
                l.DiscountPct is < 0 or > 100)
            {
                problemas.Add(
                    $"línea {i} inusable: item='{l.ItemCode}' qty={l.Quantity} precio={l.UnitPrice} " +
                    $"desc={l.DiscountPct}");
                continue;
            }

            // El expected_line_total del contrato contra la aritmética de la
            // propia línea. Desde v0.37.2 hay UNA ENTRADA POR LÍNEA DEL PEDIDO y
            // el mismo item_code puede aparecer dos veces a precios distintos —
            // una promoción "lleve 10, pague 9" es exactamente eso. Si el reparto
            // viene mal hecho, acá se ve, y se ve ANTES de escribir en SAP.
            if (l.ExpectedLineTotal is not null &&
                CalcularTotalLinea(l) != l.ExpectedLineTotal.Value)
            {
                problemas.Add(
                    $"línea {i} ({l.ItemCode}): expected_line_total {l.ExpectedLineTotal} no " +
                    $"coincide con {l.Quantity} x {l.UnitPrice} con {l.DiscountPct ?? 0}% de " +
                    $"descuento, que da {CalcularTotalLinea(l)}");
            }
        }

        return problemas;
    }

    /// <summary>
    /// Total de una línea: cantidad por precio con el descuento aplicado,
    /// redondeado a dos decimales al final.
    /// </summary>
    /// <remarks>
    /// El descuento se aplica sobre el precio COMPLETO y el redondeo va una sola
    /// vez, sobre el resultado. Redondear el precio con descuento antes de
    /// multiplicar da otro número, y es lo que hace SAP: usa el precio con
    /// descuento entero, no el <c>Price</c> de dos decimales que muestra.
    /// </remarks>
    private static decimal CalcularTotalLinea(SapOrderInvoiceLine linea) =>
        decimal.Round(
            linea.Quantity!.Value * linea.UnitPrice!.Value * (1m - (linea.DiscountPct ?? 0m) / 100m),
            2,
            MidpointRounding.AwayFromZero);

    /// <summary>
    /// Rehace la aritmética del documento y la contrasta con
    /// <c>expected_doc_total</c>. Devuelve el motivo del rechazo, o null si cuadra.
    /// </summary>
    /// <remarks>
    /// El orden es el que fija el contrato y que las facturas reales confirman:
    /// líneas con su descuento → descuento de documento sobre ese subtotal →
    /// flete sumado SIN descontar.
    /// </remarks>
    private string? VerificarTotalEsperado(
        SapOrderInvoiceSnapshot snapshot,
        List<SapOrderInvoiceLine> lineas)
    {
        if (snapshot.ExpectedDocTotal is null)
        {
            // Sin referencia no hay nada contra qué contrastar. No se inventa una.
            _logger.LogWarning(
                "La tarea no trae expected_doc_total: se factura sin poder verificar el total " +
                "antes de escribir.");
            return null;
        }

        var subtotal = lineas.Sum(CalcularTotalLinea);

        var descuento = snapshot.InvoiceDiscountPct is > 0
            ? decimal.Round(
                subtotal * snapshot.InvoiceDiscountPct.Value / 100m, 2, MidpointRounding.AwayFromZero)
            : 0m;

        var flete = snapshot.FreightAmount ?? 0m;
        var total = decimal.Round(subtotal - descuento + flete, 2, MidpointRounding.AwayFromZero);

        if (total == snapshot.ExpectedDocTotal.Value)
        {
            return null;
        }

        // Los montos van formateados como dinero: este texto termina en el
        // error_detail de la tarea y lo lee alguien que audita, no un programa.
        static string Money(decimal v) => v.ToString("F2", CultureInfo.InvariantCulture);

        return
            $"el total calculado acá no coincide con expected_doc_total: líneas {Money(subtotal)}, " +
            $"descuento de documento {Money(descuento)} ({snapshot.InvoiceDiscountPct ?? 0}%), " +
            $"flete {Money(flete)} → {Money(total)}, y el contrato espera " +
            $"{Money(snapshot.ExpectedDocTotal.Value)}. No se factura: la diferencia significa que " +
            "este integrador no está trasladando algo que el WMS sí contó";
    }

    /// <summary>
    /// Resuelve el código de un gasto adicional por su NOMBRE, contra los datos
    /// maestros de la empresa. Devuelve null si no está definido.
    /// </summary>
    /// <remarks>
    /// Ojo con los dos nombres: en el catálogo la clave es <c>ExpensCode</c> —sin
    /// la "e", typo viejo de SAP— y en la línea del documento el campo es
    /// <c>ExpenseCode</c>.
    /// </remarks>
    private async Task<int?> ResolverGastoAsync(
        ServiceLayerSession session,
        string nombre,
        CancellationToken cancellationToken)
    {
        var filtro = $"Name eq '{nombre.Replace("'", "''")}'";
        var ruta = $"AdditionalExpenses?$filter={Uri.EscapeDataString(filtro)}&$select=ExpensCode,Name";

        var (status, body) = await session
            .SendForStringAsync(() => new HttpRequestMessage(HttpMethod.Get, ruta), cancellationToken)
            .ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            throw new ServiceLayerException(
                $"No se pudo resolver el gasto adicional '{nombre}' ({(int)status}).", status, body);
        }

        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() == 0)
        {
            return null;
        }

        if (!value[0].TryGetProperty("ExpensCode", out var codigo) ||
            !codigo.TryGetInt32(out var n))
        {
            return null;
        }

        _logger.LogInformation("Gasto adicional '{Nombre}' → ExpenseCode {Codigo}.", nombre, n);
        return n;
    }

    /// <summary>
    /// Borra el borrador del ensayo y verifica que se haya ido. Devuelve una
    /// advertencia si algo quedó colgado, o null si salió limpio.
    /// </summary>
    private async Task<string?> BorrarBorradorAsync(
        ServiceLayerSession session,
        int docEntry,
        CancellationToken cancellationToken)
    {
        var (status, body) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Delete, $"Drafts({docEntry})"),
                cancellationToken)
            .ConfigureAwait(false);

        if (status is not (HttpStatusCode.NoContent or HttpStatusCode.OK))
        {
            return $"FALLÓ el borrado del borrador {docEntry} ({(int)status}). " +
                   $"HAY QUE BORRARLO A MANO: {body}";
        }

        var (statusVerif, _) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"Drafts({docEntry})"),
                cancellationToken)
            .ConfigureAwait(false);

        if (statusVerif != HttpStatusCode.NotFound)
        {
            return $"El DELETE respondió bien pero el borrador {docEntry} sigue respondiendo " +
                   $"({(int)statusVerif}).";
        }

        _logger.LogInformation("Borrador {DocEntry} borrado y verificado (404).", docEntry);
        return null;
    }

    private static InvoicePayload ArmarPayload(
        SapOrderInvoiceSnapshot snapshot,
        List<SapOrderInvoiceLine> lineas,
        string almacen,
        Dictionary<string, int> binPorCodigo,
        int? codigoFlete,
        string taxCodeFlete,
        bool soloBorrador) =>
        new()
        {
            DocObjectCode = soloBorrador ? "oInvoices" : null,
            DiscountPercent = snapshot.InvoiceDiscountPct is > 0
                ? snapshot.InvoiceDiscountPct
                : null,
            DocumentAdditionalExpenses = codigoFlete is null
                ? null
                :
                [
                    // El TaxCode viene de configuración y NO de una constante acá:
                    // es una decisión fiscal de la empresa. Se intentó leerlo de
                    // la definición del gasto, como se hizo con el ExpenseCode, y
                    // no se puede —"Domestic Freight" tiene TaxLiable tNO y los
                    // grupos de IVA vacíos— y omitirlo tampoco, porque SAP rechaza
                    // el documento con -5002 "Tax code not defined for freight".
                    new InvoiceAdditionalExpense
                    {
                        ExpenseCode = codigoFlete.Value,
                        LineTotal = snapshot.FreightAmount!.Value,
                        TaxCode = taxCodeFlete,
                    },
                ],
            CardCode = snapshot.ClientCode!,
            DocDate = snapshot.InvoiceDate!.Length >= 10
                ? snapshot.InvoiceDate[..10]
                : snapshot.InvoiceDate,
            Comments = InvoicePayload.BuildComments(
                snapshot.OrderUuid!, $"WMS orden picada, camion {snapshot.TruckId}"),
            DocumentLines = lineas
                .Select((l, i) => new InvoiceLine
                {
                    ItemCode = l.ItemCode!,
                    Quantity = l.Quantity!.Value,
                    UnitPrice = l.UnitPrice!.Value,
                    // Un discount_pct nulo se toma como 0. Tolerancia temporal: el
                    // middleware normaliza a 0.00, pero llegó nulo alguna vez.
                    DiscountPercent = l.DiscountPct ?? 0m,
                    WarehouseCode = almacen,
                    TaxCode = "Exempt",
                    DocumentLinesBinAllocations = (l.BinAllocations?.Count ?? 0) == 0
                        ? null
                        : l.BinAllocations!
                            .Select(b => new InvoiceBinAllocation
                            {
                                BinAbsEntry = binPorCodigo[b.BinCode!],
                                Quantity = b.Quantity!.Value,
                                BaseLineNumber = i,
                            })
                            .ToList(),
                })
                .ToList(),
        };

    private async Task<Dictionary<string, int>> ResolverBinsAsync(
        ServiceLayerSession session,
        List<string> binCodes,
        string almacen,
        CancellationToken cancellationToken)
    {
        var condiciones = string.Join(
            " or ", binCodes.Select(c => $"BinCode eq '{c.Replace("'", "''")}'"));
        var filtro = $"Warehouse eq '{almacen}' and ({condiciones})";
        var ruta = $"BinLocations?$filter={Uri.EscapeDataString(filtro)}&$select=AbsEntry,BinCode,Inactive";

        var (status, body) = await session
            .SendForStringAsync(() => new HttpRequestMessage(HttpMethod.Get, ruta), cancellationToken)
            .ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            throw new ServiceLayerException(
                $"No se pudieron resolver los bin_code contra BinLocations ({(int)status}).",
                status,
                body);
        }

        var mapa = new Dictionary<string, int>();
        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Array)
        {
            return mapa;
        }

        foreach (var bin in value.EnumerateArray())
        {
            if (!bin.TryGetProperty("BinCode", out var code) ||
                !bin.TryGetProperty("AbsEntry", out var abs) ||
                !abs.TryGetInt32(out var absEntry))
            {
                continue;
            }

            if (bin.TryGetProperty("Inactive", out var inactivo) && inactivo.GetString() == "tYES")
            {
                _logger.LogWarning("El bin {Codigo} está INACTIVO en SAP; no se usa.", code.GetString());
                continue;
            }

            mapa[code.GetString()!] = absEntry;
        }

        return mapa;
    }

    private static async Task<int?> BuscarFacturaExistenteAsync(
        ServiceLayerSession session,
        string orderUuid,
        CancellationToken cancellationToken)
    {
        var ruta =
            $"Invoices?$filter=substringof('{Uri.EscapeDataString(orderUuid)}', Comments) " +
            "and Cancelled eq 'tNO'&$select=DocEntry,DocNum&$top=1";

        var (status, body) = await session
            .SendForStringAsync(() => new HttpRequestMessage(HttpMethod.Get, ruta), cancellationToken)
            .ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            // No poder verificar es peor que esperar: una factura duplicada
            // duplica también la salida de inventario.
            throw new ServiceLayerException(
                $"No se pudo verificar si la factura de {orderUuid} ya existe ({(int)status}). {body}",
                status,
                body);
        }

        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("value", out var value) ||
            value.ValueKind != JsonValueKind.Array ||
            value.GetArrayLength() == 0)
        {
            return null;
        }

        return value[0].TryGetProperty("DocNum", out var docNum) && docNum.TryGetInt32(out var n)
            ? n
            : null;
    }

    private static int? LeerEntero(string body, string propiedad)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(propiedad, out var v) && v.TryGetInt32(out var n)
                ? n
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static decimal? LeerDecimal(string body, string propiedad)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty(propiedad, out var v) &&
                   v.ValueKind == JsonValueKind.Number
                ? v.GetDecimal()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>Desenlace de integrar una tarea de factura.</summary>
public sealed record OrderInvoiceOutcome(
    bool Integrada,
    int? DocNum,
    string? Error,
    bool YaExistiaEnSap = false,
    bool CreadaSinPoderLeerNumero = false,
    bool Simulada = false,
    bool EnsayoEnBorrador = false,
    int? DocEntry = null,
    decimal? DocTotal = null,
    string? Advertencia = null)
{
    /// <summary>
    /// El ensayo en borrador terminó. NO hay factura asentada: sirve para saber
    /// que el payload es válido y que la aritmética de SAP coincide con la del
    /// contrato, no para cerrar una tarea.
    /// </summary>
    public static OrderInvoiceOutcome EnsayoTerminado(
        int? docEntry, int? docNum, decimal? docTotal, string? advertencia) =>
        new(false, docNum, null,
            EnsayoEnBorrador: true,
            DocEntry: docEntry,
            DocTotal: docTotal,
            Advertencia: advertencia);

    public static OrderInvoiceOutcome SimuladaOk() => new(false, null, null, false, false, true);

    public static OrderInvoiceOutcome Creada(int docNum) => new(true, docNum, null);

    public static OrderInvoiceOutcome YaExistia(int docNum) => new(true, docNum, null, true);

    public static OrderInvoiceOutcome Rechazada(string error) => new(false, null, error);

    /// <summary>
    /// SAP la creó pero no se pudo leer el número. NO es un error reportable: la
    /// factura existe y reintentarla la duplicaría.
    /// </summary>
    public static OrderInvoiceOutcome CreadaSinNumero() =>
        new(false, null, null, false, true);
}
