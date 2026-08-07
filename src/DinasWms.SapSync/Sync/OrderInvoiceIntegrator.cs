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
    public async Task<OrderInvoiceOutcome> IntegrarAsync(
        ServiceLayerSession session,
        SapOrderInvoiceSyncTask tarea,
        CancellationToken cancellationToken,
        bool simular = false)
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
        var payload = ArmarPayload(snapshot, lineas, almacen, binPorCodigo);
        var json = payload.ToJson();

        if (simular)
        {
            _logger.LogInformation(
                "Tarea {TaskId} — SIMULACIÓN, no se envía nada. Payload que se enviaría:\n{Json}",
                tarea.TaskId,
                json);
            return OrderInvoiceOutcome.SimuladaOk();
        }

        _logger.LogInformation("Tarea {TaskId}: enviando a SAP\n{Json}", tarea.TaskId, json);

        var (status, body) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "Invoices")
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
            }
        }

        return problemas;
    }

    private static InvoicePayload ArmarPayload(
        SapOrderInvoiceSnapshot snapshot,
        List<SapOrderInvoiceLine> lineas,
        string almacen,
        Dictionary<string, int> binPorCodigo) =>
        new()
        {
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
    bool Simulada = false)
{
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
