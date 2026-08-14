using System.Net;
using System.Text.Json;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Integra UNA tarea de facturas, elegida por id. Arnés manual.
/// </summary>
/// <remarks>
/// ⚠ Con <c>--Probe:Confirm=true</c> ESCRIBE EN SAP Y ES IRREVERSIBLE: una
/// factura se ANULA, no se borra, y descuenta inventario.
///
/// Desde que las facturas corren automáticas, este arnés ya no es el camino
/// normal: sirve para empujar UNA tarea concreta y para medir el movimiento de
/// inventario alrededor de ella. El trabajo real lo hace
/// <see cref="OrderInvoiceIntegrator"/>, el mismo que usa el paso automático —
/// duplicar esa lógica sería garantizar que "como lo probamos" y "como corre
/// solo" se separen con el tiempo.
///
/// Uso:
///   --RunMode=InvoiceProbe --Probe:TaskId=15                  (simulación)
///   --RunMode=InvoiceProbe --Probe:TaskId=15 --Probe:Confirm=true
///   [--Probe:SkipReport=true]
/// </remarks>
public sealed class InvoiceProbeWorker : BackgroundService
{
    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly IMiddlewareClient _middleware;
    private readonly OrderInvoiceIntegrator _integrator;
    private readonly InvoicesOptions _invoicesOptions;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<InvoiceProbeWorker> _logger;

    public InvoiceProbeWorker(
        IServiceLayerSessionFactory sessionFactory,
        IMiddlewareClient middleware,
        OrderInvoiceIntegrator integrator,
        IOptions<InvoicesOptions> invoicesOptions,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<InvoiceProbeWorker> logger)
    {
        _sessionFactory = sessionFactory;
        _middleware = middleware;
        _integrator = integrator;
        _invoicesOptions = invoicesOptions.Value;
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
        var confirmado = Bandera("Probe:Confirm");
        var omitirReporte = Bandera("Probe:SkipReport");

        if (!int.TryParse(_configuration["Probe:TaskId"], out var taskId) || taskId <= 0)
        {
            Environment.ExitCode = 1;
            _logger.LogError("Falta Probe:TaskId. Ej: --Probe:TaskId=15");
            return;
        }

        // --- La tarea real, de la cola real -----------------------------------
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

        // NO BORRAR ESTE AGRUPAMIENTO POR item_code.
        //
        // El contrato v0.37.2 dice que las líneas van UNA POR LÍNEA DEL PEDIDO,
        // que item_code puede repetirse, y que no se agrupa por ese campo. Esa
        // regla gobierna la CONSTRUCCIÓN de la factura — armar el documento
        // agrupando fue lo que hizo que una promoción "lleve 10, pague 9"
        // facturara 11 unidades a precio de lista.
        //
        // Acá no se está armando ningún documento: se está preguntando cuánto
        // stock se movió, y el stock se mueve POR ÍTEM, no por línea. Si un ítem
        // viene en tres entradas, lo que salió del almacén es la suma de las
        // tres, y compararlo línea por línea daría "NO CUADRA" en una factura
        // perfecta. Son dos preguntas distintas sobre el mismo dato.
        //
        // El integrador —que sí arma el documento— es estrictamente posicional:
        // ver OrderInvoiceIntegrator, donde BaseLineNumber es el índice.
        var itemCodes = (tarea.OrderInvoice.Lines ?? [])
            .Select(l => l.ItemCode)
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Select(c => c!)
            .Distinct()
            .ToList();

        var almacen = _invoicesOptions.WarehouseCode;

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // --- Stock ANTES ------------------------------------------------------
        // Medir antes y después es lo que convierte "debería haberse movido" en un
        // número. Se hace incluso en simulación: deja la línea base tomada.
        var stockAntes = await LeerStockAsync(session, itemCodes, almacen, cancellationToken)
            .ConfigureAwait(false);

        foreach (var (item, cantidad) in stockAntes)
        {
            _logger.LogInformation("STOCK ANTES — {Item} en almacén {Almacen}: {Stock}", item, almacen, cantidad);
        }

        if (!confirmado)
        {
            await _integrator
                .IntegrarAsync(session, tarea, cancellationToken, simular: true)
                .ConfigureAwait(false);

            _logger.LogWarning(
                "SIMULACIÓN — no se envió nada. Esta factura es REAL e IRREVERSIBLE (se anula, no " +
                "se borra) y descuenta inventario. Hace falta --Probe:Confirm=true.");
            return;
        }

        _logger.LogWarning("Integrando la tarea {TaskId} de verdad. ESTO ESCRIBE EN SAP…", taskId);

        var outcome = await _integrator
            .IntegrarAsync(session, tarea, cancellationToken)
            .ConfigureAwait(false);

        // --- Stock DESPUÉS ----------------------------------------------------
        var stockDespues = await LeerStockAsync(session, itemCodes, almacen, cancellationToken)
            .ConfigureAwait(false);

        foreach (var item in itemCodes)
        {
            var antes = stockAntes.TryGetValue(item, out var a) ? a : (decimal?)null;
            var despues = stockDespues.TryGetValue(item, out var d) ? d : (decimal?)null;
            var facturado = (tarea.OrderInvoice.Lines ?? [])
                .Where(l => l.ItemCode == item)
                .Sum(l => l.Quantity ?? 0m);

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

        // --- Cerrar el ciclo --------------------------------------------------
        if (outcome.CreadaSinPoderLeerNumero)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "SAP aceptó la factura pero no se pudo leer su DocNum: NO se reporta nada. El " +
                "anti-duplicado del próximo ciclo lo resuelve.");
            return;
        }

        if (!outcome.Integrada)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "=== La tarea {TaskId} NO se integró: {Error} ===\n" +
                "No se reporta nada al middleware desde este arnés: qué hacer con la tarea lo decide " +
                "un humano.",
                taskId,
                outcome.Error);
            return;
        }

        _logger.LogInformation(
            "=== FACTURA {DocNum} en SAP{Nota} ===",
            outcome.DocNum,
            outcome.YaExistiaEnSap ? " (ya existía, no se creó otra)" : "");

        if (omitirReporte)
        {
            _logger.LogWarning(
                "--Probe:SkipReport=true — NO se reporta al middleware. La tarea {TaskId} queda " +
                "PENDIENTE con la factura {DocNum} ya creada en SAP.",
                taskId,
                outcome.DocNum);
            return;
        }

        await ReportarAsync(taskId, outcome.DocNum!.Value, cancellationToken).ConfigureAwait(false);
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

    private async Task ReportarAsync(int taskId, int docNum, CancellationToken cancellationToken)
    {
        var ruta = $"admin/sap-sync/order-invoices/{taskId}/result";
        var reporte = SapSyncResultReport.Integrado(docNum);

        var (status, body) = await _middleware
            .PostJsonAsync(ruta, reporte.ToJson(), cancellationToken)
            .ConfigureAwait(false);

        if (status == HttpStatusCode.OK)
        {
            _logger.LogInformation(
                "=== CICLO CERRADO. Tarea {TaskId} reportada como INTEGRADO. Respuesta:\n{Body}",
                taskId,
                string.IsNullOrWhiteSpace(body) ? "(cuerpo vacío)" : body);
            return;
        }

        Environment.ExitCode = 1;
        _logger.LogError(
            "FALLÓ el reporte del resultado ({Codigo}). La factura está en SAP y el middleware NO lo " +
            "sabe:\n{Body}",
            (int)status,
            body);
    }

    private bool Bandera(string clave) =>
        string.Equals(_configuration[clave], "true", StringComparison.OrdinalIgnoreCase);
}
