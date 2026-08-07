using System.Net;
using System.Text.Json;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.ServiceLayer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Sync;

/// <summary>
/// Integra las facturas de las órdenes ya picadas: toma las tareas pendientes,
/// crea la factura en SAP y reporta el resultado.
/// </summary>
/// <remarks>
/// El trabajo real de cada tarea vive en <see cref="OrderInvoiceIntegrator"/>,
/// compartido con el arnés manual. Acá queda solo lo del ciclo: recorrer la
/// cola, aislar cada tarea, y reportar.
///
/// Criterio: ante la duda no se postea. Una factura que no se integra queda
/// pendiente y se reintenta; una factura mal integrada saca inventario que no
/// salió y hay que anularla a mano.
/// </remarks>
public sealed class OrderInvoicesSyncStep : IDocumentSyncStep
{
    private const string RutaPendientes = "admin/sap-sync/order-invoices/pending";

    private readonly IMiddlewareClient _middleware;
    private readonly OrderInvoiceIntegrator _integrator;
    private readonly MiddlewareOptions _middlewareOptions;
    private readonly ILogger<OrderInvoicesSyncStep> _logger;

    public OrderInvoicesSyncStep(
        IMiddlewareClient middleware,
        OrderInvoiceIntegrator integrator,
        IOptions<MiddlewareOptions> middlewareOptions,
        ILogger<OrderInvoicesSyncStep> logger)
    {
        _middleware = middleware;
        _integrator = integrator;
        _middlewareOptions = middlewareOptions.Value;
        _logger = logger;
    }

    public string Name => "OrderInvoices";

    public async Task<bool> HasPendingWorkAsync(CancellationToken cancellationToken)
    {
        // A propósito NO se hace login acá: se reusa el token que haya. A 20
        // segundos, un login por sondeo serían ~4.300 logins diarios contra el
        // middleware para preguntar casi siempre "no hay nada". El cliente ya
        // resuelve el 401 con re-login y un reintento, así que la primera sonda
        // del proceso paga un viaje de más y las demás van con el token vivo.
        var tareas = await ObtenerPendientesAsync(cancellationToken).ConfigureAwait(false);
        return tareas.Count > 0;
    }

    public async Task<DocumentSyncStepResult> ExecuteAsync(
        ServiceLayerSession session,
        CancellationToken cancellationToken)
    {
        await _middleware.LoginAsync(cancellationToken).ConfigureAwait(false);

        var tareas = await ObtenerPendientesAsync(cancellationToken).ConfigureAwait(false);

        if (tareas.Count == 0)
        {
            return new DocumentSyncStepResult(0, 0, "sin tareas pendientes");
        }

        var aProcesar = tareas.Take(_middlewareOptions.MaxTasksPerCycle).ToList();

        if (tareas.Count > aProcesar.Count)
        {
            // Nunca truncar en silencio.
            _logger.LogWarning(
                "Hay {Total} facturas pendientes y el tope por ciclo es {Tope}. Se procesan " +
                "{Procesa}; el resto queda para el próximo ciclo.",
                tareas.Count,
                _middlewareOptions.MaxTasksPerCycle,
                aProcesar.Count);
        }

        var integrados = 0;
        var fallidos = 0;

        foreach (var tarea in aProcesar)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Aislamiento por tarea: una factura problemática no frena las demás.
            try
            {
                var outcome = await _integrator
                    .IntegrarAsync(session, tarea, cancellationToken)
                    .ConfigureAwait(false);

                if (outcome.CreadaSinPoderLeerNumero)
                {
                    // Existe en SAP pero sin número legible: no se reporta nada, ni
                    // éxito ni error. El anti-duplicado del próximo ciclo la
                    // encuentra y la reporta bien.
                    fallidos++;
                    continue;
                }

                if (outcome.Integrada)
                {
                    integrados++;
                    _logger.LogInformation(
                        "Tarea {TaskId}: factura {DocNum} en SAP{Nota}.",
                        tarea.TaskId,
                        outcome.DocNum,
                        outcome.YaExistiaEnSap ? " (ya existía, no se creó otra)" : "");

                    await ReportarAsync(
                        tarea.TaskId,
                        SapSyncResultReport.Integrado(outcome.DocNum!.Value),
                        cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    fallidos++;
                    _logger.LogError("Tarea {TaskId} NO integrada: {Error}", tarea.TaskId, outcome.Error);

                    await ReportarAsync(
                        tarea.TaskId,
                        SapSyncResultReport.Error(outcome.Error ?? "sin detalle"),
                        cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                fallidos++;
                _logger.LogError(ex, "Tarea {TaskId} falló de forma inesperada.", tarea.TaskId);

                await ReportarAsync(
                    tarea.TaskId,
                    SapSyncResultReport.Error($"Error inesperado: {ex.Message}"),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return new DocumentSyncStepResult(
            integrados, fallidos, $"{aProcesar.Count} tarea(s) de {tareas.Count} pendientes");
    }

    private async Task<List<SapOrderInvoiceSyncTask>> ObtenerPendientesAsync(
        CancellationToken cancellationToken)
    {
        var (status, body) = await _middleware
            .GetAsync(RutaPendientes, cancellationToken)
            .ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            throw new MiddlewareException(
                $"El middleware devolvió {(int)status} {status} al pedir la cola de facturas. {body}",
                status,
                body);
        }

        try
        {
            var pagina = JsonSerializer.Deserialize<SapOrderInvoiceSyncTasksPage>(body, SapSyncJson.Options);
            return pagina?.Tasks ?? [];
        }
        catch (JsonException ex)
        {
            throw new MiddlewareException(
                "La cola de facturas respondió 200 pero el cuerpo no es JSON interpretable. " + body,
                status,
                body,
                ex);
        }
    }

    private async Task ReportarAsync(
        int taskId,
        SapSyncResultReport reporte,
        CancellationToken cancellationToken)
    {
        var ruta = $"admin/sap-sync/order-invoices/{taskId}/result";

        var (status, body) = await _middleware
            .PostJsonAsync(ruta, reporte.ToJson(), cancellationToken)
            .ConfigureAwait(false);

        if (status == HttpStatusCode.OK)
        {
            _logger.LogInformation(
                "Tarea {TaskId}: reportada como {Estado}{Ref}.",
                taskId,
                reporte.Status,
                reporte.SapReference is null ? "" : $" (sap_reference {reporte.SapReference})");
            return;
        }

        // Un reporte fallido es serio: la factura puede estar en SAP y el
        // middleware no saberlo. Se registra fuerte, pero no se lanza — las demás
        // tareas del ciclo deben seguir, y el anti-duplicado cubre el reintento.
        _logger.LogError(
            "Tarea {TaskId}: FALLÓ el reporte del resultado ({Codigo} {Status}). El middleware puede " +
            "quedar desincronizado con SAP hasta el próximo ciclo. Respuesta: {Body}",
            taskId,
            (int)status,
            status,
            body);
    }
}
