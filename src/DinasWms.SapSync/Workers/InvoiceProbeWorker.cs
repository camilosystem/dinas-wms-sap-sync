using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.ServiceLayer.Invoices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Crea UNA factura real en SAP a partir de una tarea real de la cola
/// <c>order-invoices</c>, y cierra el ciclo reportando el resultado al
/// middleware.
/// </summary>
/// <remarks>
/// ⚠ ESCRIBE EN SAP Y ES IRREVERSIBLE: una factura se ANULA, no se borra, y
/// además descuenta inventario. Por eso exige <c>--Probe:Confirm=true</c>: sin
/// ese flag arma el payload, lo muestra, y no envía nada.
///
/// El snapshot NO se transcribe a mano: se toma de la cola real, para que lo que
/// se prueba sea el camino de verdad y no una copia idealizada.
///
/// Uso:
///   --RunMode=InvoiceProbe --Probe:TaskId=15 [--Probe:WarehouseCode=01]
///   [--Probe:SkipReport=true] --Probe:Confirm=true
/// </remarks>
public sealed class InvoiceProbeWorker : BackgroundService
{
    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly IMiddlewareClient _middleware;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<InvoiceProbeWorker> _logger;

    public InvoiceProbeWorker(
        IServiceLayerSessionFactory sessionFactory,
        IMiddlewareClient middleware,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<InvoiceProbeWorker> logger)
    {
        _sessionFactory = sessionFactory;
        _middleware = middleware;
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
            _logger.LogError(ex, "PRUEBA DE FACTURA FALLIDA. {Message}", ex.Message);
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task CorrerAsync(CancellationToken cancellationToken)
    {
        var almacen = _configuration["Probe:WarehouseCode"] ?? "01";
        var confirmado = Bandera("Probe:Confirm");
        var omitirReporte = Bandera("Probe:SkipReport");

        if (!int.TryParse(_configuration["Probe:TaskId"], out var taskId) || taskId <= 0)
        {
            Environment.ExitCode = 1;
            _logger.LogError("Falta Probe:TaskId. Ej: --Probe:TaskId=15");
            return;
        }

        // --- 1. La tarea real, de la cola real --------------------------------
        await _middleware.LoginAsync(cancellationToken).ConfigureAwait(false);

        var tarea = await BuscarTareaAsync(taskId, cancellationToken).ConfigureAwait(false);

        if (tarea?.OrderInvoice is null)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "La tarea {TaskId} no está en la cola de pendientes, o no trae order_invoice. " +
                "No se inventa un snapshot.",
                taskId);
            return;
        }

        var snapshot = tarea.OrderInvoice;
        var lineas = snapshot.Lines ?? [];

        if (lineas.Count == 0)
        {
            Environment.ExitCode = 1;
            _logger.LogError("La tarea {TaskId} no trae líneas. No se factura un documento vacío.", taskId);
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.OrderUuid) ||
            string.IsNullOrWhiteSpace(snapshot.ClientCode) ||
            string.IsNullOrWhiteSpace(snapshot.InvoiceDate))
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "La tarea {TaskId} no trae order_uuid, client_code o invoice_date. No se integra.",
                taskId);
            return;
        }

        var problemas = lineas
            .Select((l, i) => (l, i))
            .Where(x => string.IsNullOrWhiteSpace(x.l.ItemCode) ||
                        x.l.Quantity is null or <= 0 ||
                        x.l.UnitPrice is null or < 0 ||
                        x.l.DiscountPct is < 0 or > 100)
            .Select(x => $"línea {x.i}: item='{x.l.ItemCode}' qty={x.l.Quantity} " +
                         $"precio={x.l.UnitPrice} desc={x.l.DiscountPct}")
            .ToList();

        if (problemas.Count > 0)
        {
            // Precio 0 y descuento 100 SÍ son válidos (promociones). Lo que no se
            // acepta es una cantidad no positiva, un precio negativo o un
            // descuento fuera de rango.
            Environment.ExitCode = 1;
            _logger.LogError(
                "La tarea {TaskId} trae líneas inusables: {Problemas}", taskId, string.Join("; ", problemas));
            return;
        }

        // Un discount_pct nulo se toma como 0 (sin descuento). Es una TOLERANCIA
        // temporal, no el contrato: el middleware va a normalizarlo para que
        // siempre emita 0.00. Mientras tanto se registra cuáles llegaron nulas,
        // porque es una interpretación nuestra y no debe quedar invisible.
        var sinDescuento = lineas
            .Select((l, i) => (l, i))
            .Where(x => x.l.DiscountPct is null)
            .Select(x => $"{x.i} ({x.l.ItemCode})")
            .ToList();

        if (sinDescuento.Count > 0)
        {
            _logger.LogWarning(
                "Línea(s) {Lineas} llegaron con discount_pct = null. Se interpretan como 0 (sin " +
                "descuento). Es una asunción de este lado, no un dato del contrato.",
                string.Join(", ", sinDescuento));
        }

        // --- 2. Las asignaciones de bin --------------------------------------
        // El reparto lo hizo el middleware y se usa TAL CUAL. Lo único que se
        // hace acá es (a) traducir bin_code → AbsEntry, que es la clave interna
        // que exige SAP, y (b) comprobar que el reparto sume la cantidad de la
        // línea. Comprobar no es recalcular: si no cuadra, se aborta.
        var codigosBin = lineas
            .SelectMany(l => l.BinAllocations ?? [])
            .Select(b => b.BinCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct()
            .ToList();

        var binPorCodigo = new Dictionary<string, int>();

        if (codigosBin.Count > 0)
        {
            await using var sesionBins = await _sessionFactory
                .OpenAsync(cancellationToken)
                .ConfigureAwait(false);

            binPorCodigo = await ResolverBinsAsync(
                sesionBins, codigosBin, almacen, cancellationToken).ConfigureAwait(false);

            var sinResolver = codigosBin.Where(c => !binPorCodigo.ContainsKey(c)).ToList();

            if (sinResolver.Count > 0)
            {
                Environment.ExitCode = 1;
                _logger.LogError(
                    "Estos bin_code no existen en el almacén {Almacen} de SAP: {Codigos}. No se " +
                    "inventa una ubicación.",
                    almacen,
                    string.Join(", ", sinResolver));
                return;
            }
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
                descuadres.Add($"línea {i} ({lineas[i].ItemCode}): alguna asignación viene sin cantidad");
                continue;
            }

            var sumaBins = asignaciones.Sum(b => b.Quantity!.Value);

            if (sumaBins != lineas[i].Quantity!.Value)
            {
                descuadres.Add(
                    $"línea {i} ({lineas[i].ItemCode}): los bins suman {sumaBins} pero la línea " +
                    $"factura {lineas[i].Quantity}");
            }

            aproximadas += asignaciones.Count(b => b.Approximate);

            foreach (var b in asignaciones)
            {
                _logger.LogInformation(
                    "BIN línea {Linea} ({Item}): {Codigo} (AbsEntry {Abs}) × {Cantidad}{Aprox}",
                    i,
                    lineas[i].ItemCode,
                    b.BinCode,
                    binPorCodigo[b.BinCode!],
                    b.Quantity,
                    b.Approximate ? " — APROXIMADA" : "");
            }
        }

        if (descuadres.Count > 0)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "El reparto por bins no cuadra con las cantidades facturadas: {Descuadres}. No se " +
                "integra: una salida de inventario que no coincide con lo facturado hay que " +
                "corregirla a mano después.",
                string.Join("; ", descuadres));
            return;
        }

        if (aproximadas > 0)
        {
            _logger.LogWarning(
                "{Cuantas} asignación(es) vienen marcadas approximate=true: el WMS no sabe de qué " +
                "bin salió realmente la mercancía y repartió por saldo. SAP va a registrar una " +
                "salida por ubicación que puede no ser la física.",
                aproximadas);
        }

        // --- 3. El payload ----------------------------------------------------
        var payload = new InvoicePayload
        {
            CardCode = snapshot.ClientCode!,
            DocDate = snapshot.InvoiceDate!.Length >= 10
                ? snapshot.InvoiceDate[..10]
                : snapshot.InvoiceDate,
            Comments = InvoicePayload.BuildComments(
                snapshot.OrderUuid!,
                $"WMS orden picada, camion {snapshot.TruckId}"),
            DocumentLines = lineas
                .Select((l, i) => new InvoiceLine
                {
                    ItemCode = l.ItemCode!,
                    Quantity = l.Quantity!.Value,
                    UnitPrice = l.UnitPrice!.Value,
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

        // El total autoritativo es el del contrato. El calculado localmente se
        // usa solo para cruzarlo: se calcula como lo hace SAP (con el precio con
        // descuento SIN redondear), así que si los dos no coinciden, la
        // diferencia de fórmula quedó al descubierto ANTES de escribir nada.
        var totalCalculado = Math.Round(
            lineas.Sum(l => l.Quantity!.Value * l.UnitPrice!.Value * (1m - (l.DiscountPct ?? 0m) / 100m)),
            2,
            MidpointRounding.AwayFromZero);

        var totalEsperado = snapshot.ExpectedDocTotal ?? totalCalculado;

        if (snapshot.ExpectedDocTotal is null)
        {
            _logger.LogWarning(
                "El snapshot no trae expected_doc_total; se usa el total calculado localmente " +
                "({Calculado}) como referencia.",
                totalCalculado.ToString("F2", CultureInfo.InvariantCulture));
        }
        else if (snapshot.ExpectedDocTotal.Value != totalCalculado)
        {
            _logger.LogWarning(
                "El expected_doc_total del contrato ({Contrato}) NO coincide con el total calculado " +
                "línea por línea ({Calculado}). Manda el del contrato, pero la diferencia de fórmula " +
                "es real y hay que resolverla.",
                snapshot.ExpectedDocTotal.Value.ToString("F2", CultureInfo.InvariantCulture),
                totalCalculado.ToString("F2", CultureInfo.InvariantCulture));
        }

        var json = payload.ToJson();

        _logger.LogInformation(
            "=== FACTURA REAL a enviar (POST /Invoices) ===\n" +
            "  Tarea {TaskId} | order_uuid {Uuid} | cliente {Cliente} | {Lineas} líneas | " +
            "almacén {Almacen}\n" +
            "  expected_doc_total (contrato) = {Total} | calculado localmente = {Calculado}\n{Json}",
            taskId,
            snapshot.OrderUuid,
            snapshot.ClientCode,
            lineas.Count,
            almacen,
            totalEsperado.ToString("F2", CultureInfo.InvariantCulture),
            totalCalculado.ToString("F2", CultureInfo.InvariantCulture),
            json);

        if (!confirmado)
        {
            _logger.LogWarning(
                "SIMULACIÓN — no se envió nada. Esta factura es REAL e IRREVERSIBLE (se anula, no " +
                "se borra) y descuenta inventario. Hace falta --Probe:Confirm=true.");
            return;
        }

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // --- 3. Anti-duplicado, JUSTO antes de escribir -----------------------
        var yaExiste = await BuscarFacturaExistenteAsync(session, snapshot.OrderUuid!, cancellationToken)
            .ConfigureAwait(false);

        if (yaExiste is not null)
        {
            _logger.LogWarning(
                "ANTI-DUPLICADO: la factura del order_uuid {Uuid} YA existe en SAP (DocNum {DocNum}) " +
                "y no está anulada. NO se crea otra.",
                snapshot.OrderUuid,
                yaExiste);

            if (!omitirReporte)
            {
                await ReportarAsync(
                    taskId, SapSyncResultReport.Integrado(yaExiste.Value), cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        _logger.LogInformation(
            "ANTI-DUPLICADO: no hay ninguna factura no anulada con el order_uuid {Uuid}. Se procede.",
            snapshot.OrderUuid);

        // --- 4. Stock ANTES ---------------------------------------------------
        var itemCodes = lineas.Select(l => l.ItemCode!).Distinct().ToList();
        var stockAntes = await LeerStockAsync(session, itemCodes, almacen, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (item, cantidad) in stockAntes)
        {
            _logger.LogInformation("STOCK ANTES — {Item} en almacén {Almacen}: {Stock}", item, almacen, cantidad);
        }

        // --- 5. POST real -----------------------------------------------------
        _logger.LogWarning("Enviando POST /Invoices a SAP. ESTO ES REAL E IRREVERSIBLE…");

        var (status, body) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "Invoices")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                },
                cancellationToken)
            .ConfigureAwait(false);

        // La respuesta literal COMPLETA es la evidencia. No se resume ni se recorta.
        _logger.LogInformation(
            "=== RESPUESTA LITERAL DE SAP ({Codigo} {Status}), {Bytes:N0} bytes ===\n{Body}",
            (int)status,
            status,
            body.Length,
            body);

        if (status is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            // No se reporta ERROR al middleware desde el arnés: la primera vez
            // que esto falle, la decisión de qué hacer con la tarea es de un
            // humano, no de una prueba.
            Environment.ExitCode = 1;
            _logger.LogError(
                "=== SAP RECHAZÓ la factura ({Codigo}). No se reporta nada al middleware; la tarea " +
                "{TaskId} queda PENDIENTE tal como estaba. ===",
                (int)status,
                taskId);
            return;
        }

        var docNum = LeerEntero(body, "DocNum");
        var docEntry = LeerEntero(body, "DocEntry");
        var docTotal = LeerDecimal(body, "DocTotal");

        _logger.LogInformation(
            "=== FACTURA CREADA: DocNum {DocNum}, DocEntry {DocEntry} ===", docNum, docEntry);

        // --- 6. DocTotal esperado vs real ------------------------------------
        if (docTotal is null)
        {
            Environment.ExitCode = 1;
            _logger.LogError("No se pudo leer el DocTotal de la respuesta. Revisar el cuerpo literal.");
        }
        else if (docTotal.Value == totalEsperado)
        {
            _logger.LogInformation(
                "DocTotal: SAP {Real} == esperado {Esperado}. COINCIDE.",
                docTotal.Value.ToString("F2", CultureInfo.InvariantCulture),
                totalEsperado.ToString("F2", CultureInfo.InvariantCulture));
        }
        else
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "DocTotal: SAP {Real} != esperado {Esperado}. DIFERENCIA de {Diferencia}. La factura " +
                "YA EXISTE con el total de SAP; esto se reporta, no se corrige solo.",
                docTotal.Value.ToString("F2", CultureInfo.InvariantCulture),
                totalEsperado.ToString("F2", CultureInfo.InvariantCulture),
                (docTotal.Value - totalEsperado).ToString("F2", CultureInfo.InvariantCulture));
        }

        // --- 7. ¿Hizo falta asignar bins? ------------------------------------
        ReportarBins(body);

        // --- 8. Stock DESPUÉS -------------------------------------------------
        var stockDespues = await LeerStockAsync(session, itemCodes, almacen, cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in itemCodes)
        {
            var antes = stockAntes.TryGetValue(item, out var a) ? a : (decimal?)null;
            var despues = stockDespues.TryGetValue(item, out var d) ? d : (decimal?)null;
            var facturado = lineas.Where(l => l.ItemCode == item).Sum(l => l.Quantity!.Value);

            if (antes is null || despues is null)
            {
                _logger.LogWarning("STOCK — {Item}: no se pudo leer antes o después.", item);
                continue;
            }

            var movimiento = antes.Value - despues.Value;

            _logger.LogInformation(
                "STOCK {Item} en almacén {Almacen}: antes {Antes}, después {Despues}, " +
                "descontado {Movimiento}, facturado {Facturado} → {Veredicto}",
                item,
                almacen,
                antes,
                despues,
                movimiento,
                facturado,
                movimiento == facturado ? "CUADRA" : "NO CUADRA");

            if (movimiento != facturado)
            {
                Environment.ExitCode = 1;
            }
        }

        // --- 9. Cerrar el ciclo con el middleware ----------------------------
        if (docNum is null)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "SAP aceptó la factura pero no se pudo leer su DocNum, así que NO se reporta nada: " +
                "reportar ERROR sería mentir (la factura existe) y reintentar la duplicaría. El " +
                "anti-duplicado del próximo ciclo lo resuelve.");
            return;
        }

        if (omitirReporte)
        {
            _logger.LogWarning(
                "--Probe:SkipReport=true — NO se reporta al middleware. La tarea {TaskId} queda " +
                "PENDIENTE con la factura {DocNum} ya creada en SAP.",
                taskId,
                docNum);
            return;
        }

        await ReportarAsync(taskId, SapSyncResultReport.Integrado(docNum.Value), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<SapOrderInvoiceSyncTask?> BuscarTareaAsync(
        int taskId,
        CancellationToken cancellationToken)
    {
        const string ruta = "admin/sap-sync/order-invoices/pending";

        var (status, body) = await _middleware.GetAsync(ruta, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "GET {Ruta} → ({Codigo} {Status}), {Bytes:N0} bytes:\n{Body}",
            ruta,
            (int)status,
            status,
            body.Length,
            body);

        if (status != HttpStatusCode.OK)
        {
            throw new MiddlewareException(
                $"El middleware devolvió {(int)status} al pedir la cola de facturas.", status, body);
        }

        var pagina = JsonSerializer.Deserialize<SapOrderInvoiceSyncTasksPage>(body, SapSyncJson.Options);
        return pagina?.Tasks?.FirstOrDefault(t => t.TaskId == taskId);
    }

    /// <summary>
    /// Busca una factura no anulada cuyo <c>Comments</c> tenga este
    /// <c>order_uuid</c>. Mismo criterio que en pagos: las anuladas se excluyen a
    /// propósito, porque una factura anulada debe poder rehacerse.
    /// </summary>
    private async Task<int?> BuscarFacturaExistenteAsync(
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

        _logger.LogInformation(
            "ANTI-DUPLICADO GET → ({Codigo}) {Body}", (int)status, body);

        if (status != HttpStatusCode.OK)
        {
            // Si no se puede verificar, abortar es más seguro que arriesgar una
            // factura duplicada, que además duplicaría la salida de inventario.
            throw new ServiceLayerException(
                $"No se pudo verificar si la factura de {orderUuid} ya existe ({(int)status}).",
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

    /// <summary>
    /// Traduce los <c>bin_code</c> del contrato al <c>AbsEntry</c> que exige SAP,
    /// contra la entidad <c>BinLocations</c> del almacén indicado.
    /// </summary>
    private async Task<Dictionary<string, int>> ResolverBinsAsync(
        ServiceLayerSession session,
        List<string> binCodes,
        string almacen,
        CancellationToken cancellationToken)
    {
        // Comillas simples duplicadas: es como OData escapa una comilla dentro de
        // un literal. Los códigos de bin llevan acentos, así que la URL entera se
        // escapa después.
        var condiciones = string.Join(
            " or ",
            binCodes.Select(c => $"BinCode eq '{c.Replace("'", "''")}'"));

        var filtro = $"Warehouse eq '{almacen}' and ({condiciones})";
        var ruta = $"BinLocations?$filter={Uri.EscapeDataString(filtro)}&$select=AbsEntry,BinCode,Inactive";

        var (status, body) = await session
            .SendForStringAsync(() => new HttpRequestMessage(HttpMethod.Get, ruta), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation("RESOLUCIÓN DE BINS → ({Codigo})\n{Body}", (int)status, body);

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

            // Un bin inactivo no sirve para sacar mercancía: mejor descubrirlo
            // acá, como "no resuelto", que en el rechazo de SAP.
            if (bin.TryGetProperty("Inactive", out var inactivo) &&
                inactivo.GetString() == "tYES")
            {
                _logger.LogWarning("El bin {Codigo} está INACTIVO en SAP; no se usa.", code.GetString());
                continue;
            }

            mapa[code.GetString()!] = absEntry;
        }

        return mapa;
    }

    private async Task<Dictionary<string, decimal>> LeerStockAsync(
        ServiceLayerSession session,
        List<string> itemCodes,
        string almacen,
        CancellationToken cancellationToken)
    {
        var stock = new Dictionary<string, decimal>();

        foreach (var item in itemCodes)
        {
            var ruta = $"Items('{Uri.EscapeDataString(item)}')?$select=ItemCode,ItemWarehouseInfoCollection";

            var (status, body) = await session
                .SendForStringAsync(() => new HttpRequestMessage(HttpMethod.Get, ruta), cancellationToken)
                .ConfigureAwait(false);

            if (status != HttpStatusCode.OK)
            {
                _logger.LogWarning("No se pudo leer el stock de {Item} ({Codigo}).", item, (int)status);
                continue;
            }

            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("ItemWarehouseInfoCollection", out var almacenes) ||
                almacenes.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var w in almacenes.EnumerateArray())
            {
                if (w.TryGetProperty("WarehouseCode", out var code) &&
                    code.GetString() == almacen &&
                    w.TryGetProperty("InStock", out var inStock) &&
                    inStock.ValueKind == JsonValueKind.Number)
                {
                    stock[item] = inStock.GetDecimal();
                    break;
                }
            }
        }

        return stock;
    }

    /// <summary>
    /// Reporta si SAP resolvió la ubicación solo o si quedó constancia de una
    /// asignación de bin en el documento creado.
    /// </summary>
    private void ReportarBins(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);

            if (!doc.RootElement.TryGetProperty("DocumentLines", out var lineas) ||
                lineas.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var conAsignacion = 0;
            var total = 0;

            foreach (var linea in lineas.EnumerateArray())
            {
                total++;

                if (linea.TryGetProperty("DocumentLinesBinAllocations", out var bins) &&
                    bins.ValueKind == JsonValueKind.Array &&
                    bins.GetArrayLength() > 0)
                {
                    conAsignacion++;
                }
            }

            _logger.LogInformation(
                "BINS — {ConAsignacion} de {Total} líneas volvieron con DocumentLinesBinAllocations. " +
                "Si son menos de las que se enviaron, SAP resolvió parte de la ubicación por su " +
                "cuenta (AutoAllocOnIssue=SingleChoiceOnly en el almacén 01).",
                conAsignacion,
                total);
        }
        catch (JsonException)
        {
            _logger.LogWarning("No se pudo revisar las asignaciones de bin en la respuesta.");
        }
    }

    private async Task ReportarAsync(
        int taskId,
        SapSyncResultReport reporte,
        CancellationToken cancellationToken)
    {
        var ruta = $"admin/sap-sync/order-invoices/{taskId}/result";
        var cuerpo = reporte.ToJson();

        _logger.LogInformation("POST {Ruta}\n{Cuerpo}", ruta, cuerpo);

        var (status, body) = await _middleware
            .PostJsonAsync(ruta, cuerpo, cancellationToken)
            .ConfigureAwait(false);

        if (status == HttpStatusCode.OK)
        {
            _logger.LogInformation(
                "=== CICLO CERRADO. Tarea {TaskId} reportada como {Estado}. Respuesta literal del " +
                "middleware:\n{Body}",
                taskId,
                reporte.Status,
                string.IsNullOrWhiteSpace(body) ? "(cuerpo vacío)" : body);
            return;
        }

        Environment.ExitCode = 1;
        _logger.LogError(
            "FALLÓ el reporte del resultado ({Codigo} {Status}). La factura está en SAP y el " +
            "middleware NO lo sabe. Respuesta literal:\n{Body}",
            (int)status,
            status,
            body);
    }

    private bool Bandera(string clave) =>
        string.Equals(_configuration[clave], "true", StringComparison.OrdinalIgnoreCase);

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
