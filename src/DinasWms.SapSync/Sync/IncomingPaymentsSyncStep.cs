using System.Net;
using System.Text;
using System.Text.Json;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.ServiceLayer.Payments;
using DinasWms.SapSync.Sql;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Sync;

/// <summary>
/// Integra los pagos de cartera aprobados: toma las tareas pendientes del
/// middleware, crea el <c>IncomingPayment</c> en SAP, y reporta el resultado.
/// </summary>
/// <remarks>
/// Une todo lo que se validó por separado contra datos reales: la resolución de
/// <c>DocEntry</c> por SQL, el armado del payload por método de pago, y la sesión
/// por ciclo de Service Layer.
///
/// Criterio general: <b>ante la duda, no se postea</b>. Un pago que no se integra
/// queda pendiente y se reintenta; un pago mal integrado mueve dinero al lugar
/// equivocado y hay que anularlo a mano.
/// </remarks>
public sealed class IncomingPaymentsSyncStep : IDocumentSyncStep
{
    private readonly IMiddlewareClient _middleware;
    private readonly IDocEntryResolver _resolver;
    private readonly PaymentsOptions _paymentsOptions;
    private readonly MiddlewareOptions _middlewareOptions;
    private readonly Observability.SyncStatus? _status;
    private readonly ILogger<IncomingPaymentsSyncStep> _logger;

    /// <summary>Última omisión anunciada, para no repetir el aviso en cada sondeo.</summary>
    private string _ultimaOmisionAnunciada = "";

    public IncomingPaymentsSyncStep(
        IMiddlewareClient middleware,
        IDocEntryResolver resolver,
        IOptions<PaymentsOptions> paymentsOptions,
        IOptions<MiddlewareOptions> middlewareOptions,
        ILogger<IncomingPaymentsSyncStep> logger,
        Observability.SyncStatus? status = null)
    {
        _middleware = middleware;
        _resolver = resolver;
        _paymentsOptions = paymentsOptions.Value;
        _middlewareOptions = middlewareOptions.Value;
        _status = status;
        _logger = logger;

        // La decisión vigente se publica desde el arranque, aunque ninguna de
        // esas tareas esté hoy en la cola. Es lo que impide que la omisión sea
        // invisible mientras no se la ejercite.
        _status?.RegistrarOmisionConfigurada(_paymentsOptions.TaskIdsOmitidos ?? []);
    }

    /// <summary>
    /// Separa las tareas que hay que intentar de las omitidas por configuración.
    /// </summary>
    /// <remarks>
    /// Función pura y pública para poder comprobar la propiedad que importa: que
    /// excluye el <c>task_id</c> exacto y <b>ninguno más</b>. Una exclusión
    /// demasiado ancha se ve idéntica a una correcta mientras haya una sola
    /// tarea en la cola.
    /// </remarks>
    public static (List<SapAccountPaymentSyncTask> AProcesar, List<int> Omitidas) RepartirPorOmision(
        IReadOnlyList<SapAccountPaymentSyncTask> tareas,
        IReadOnlyCollection<int>? taskIdsOmitidos)
    {
        if (taskIdsOmitidos is null || taskIdsOmitidos.Count == 0)
        {
            // Lista vacía ⇒ no se omite nada. Es la vuelta atrás: vaciar la
            // configuración devuelve las tareas al ciclo sin tocar nada más.
            return (tareas.ToList(), []);
        }

        var omitir = new HashSet<int>(taskIdsOmitidos);

        var aProcesar = new List<SapAccountPaymentSyncTask>(tareas.Count);
        var omitidas = new List<int>();

        foreach (var t in tareas)
        {
            if (omitir.Contains(t.TaskId))
            {
                omitidas.Add(t.TaskId);
            }
            else
            {
                aProcesar.Add(t);
            }
        }

        return (aProcesar, omitidas);
    }

    public string Name => "IncomingPayments";

    public async Task<bool> HasPendingWorkAsync(CancellationToken cancellationToken)
    {
        // Solo middleware: ninguna sesión de Service Layer. Es lo que permite
        // sondear seguido sin gastar licencias de SAP. Tampoco se hace login: se
        // reusa el token vivo, y el 401 ya se resuelve con re-login y un
        // reintento dentro del cliente.
        var tareas = await ObtenerPendientesAsync(cancellationToken).ConfigureAwait(false);
        return tareas.Count > 0;
    }

    public async Task<DocumentSyncStepResult> ExecuteAsync(
        ServiceLayerSession session,
        CancellationToken cancellationToken)
    {
        // Login por ciclo, igual que con Service Layer: no se mantiene el token
        // vivo entre ciclos ni se renueva de forma proactiva.
        await _middleware.LoginAsync(cancellationToken).ConfigureAwait(false);

        var tareas = await ObtenerPendientesAsync(cancellationToken).ConfigureAwait(false);

        if (tareas.Count == 0)
        {
            return new DocumentSyncStepResult(0, 0, "sin tareas pendientes");
        }

        var aProcesar = tareas.Take(_middlewareOptions.MaxTasksPerCycle).ToList();

        if (tareas.Count > aProcesar.Count)
        {
            // Nunca truncar en silencio: si quedan tareas afuera, hay que decirlo.
            _logger.LogWarning(
                "Hay {Total} tareas pendientes y el tope por ciclo es {Tope}. Se procesan {Procesa} " +
                "y el resto queda para el próximo ciclo.",
                tareas.Count,
                _middlewareOptions.MaxTasksPerCycle,
                aProcesar.Count);
        }

        var integrados = 0;
        var fallidos = 0;

        foreach (var tarea in aProcesar)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Aislamiento por tarea: un pago problemático no debe frenar los demás.
            try
            {
                var ok = await ProcesarTareaAsync(session, tarea, cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    integrados++;
                }
                else
                {
                    fallidos++;
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
            integrados,
            fallidos,
            $"{aProcesar.Count} tarea(s) tomadas de {tareas.Count} pendientes");
    }

    private async Task<List<SapAccountPaymentSyncTask>> ObtenerPendientesAsync(
        CancellationToken cancellationToken)
    {
        const string ruta = "admin/sap-sync/account-payments/pending";

        var (status, body) = await _middleware.GetAsync(ruta, cancellationToken).ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            throw new MiddlewareException(
                $"El middleware devolvió {(int)status} {status} al pedir la cola de pendientes. " +
                $"Respuesta: {body}",
                status,
                body);
        }

        SapAccountPaymentSyncTasksPage? pagina;
        try
        {
            pagina = JsonSerializer.Deserialize<SapAccountPaymentSyncTasksPage>(body, SapSyncJson.Options);
        }
        catch (JsonException ex)
        {
            throw new MiddlewareException(
                "La cola de pendientes respondió 200 pero el cuerpo no es JSON interpretable. " +
                "Respuesta: " + body,
                status,
                body,
                ex);
        }

        var tareas = pagina?.Tasks ?? [];

        // El filtro va acá y no en ExecuteAsync a propósito: esta misma consulta
        // es la que usa HasPendingWorkAsync. Si lo único pendiente está omitido,
        // el worker ve "sin trabajo", no abre sesión con SAP, y su contador de
        // fallos consecutivos vuelve a cero solo. Filtrar más adelante dejaría
        // un ciclo corriendo en vacío cada 20 segundos.
        var (aProcesar, omitidas) = RepartirPorOmision(tareas, _paymentsOptions.TaskIdsOmitidos);

        _status?.RegistrarOmitidasEnLaCola(omitidas);

        var firma = string.Join(",", omitidas);

        if (firma != _ultimaOmisionAnunciada)
        {
            _ultimaOmisionAnunciada = firma;

            if (omitidas.Count > 0)
            {
                // Warning y no Information: que algo se esté salteando tiene que
                // saltar a la vista de quien lee el log, no esconderse en el ruido.
                _logger.LogWarning(
                    "OMITIDAS por configuración ({Cuantas}): task_id {Ids}. Siguen PENDIENTES en la " +
                    "cola, con su payload y su error_detail intactos; este proceso no las intenta y " +
                    "no las cuenta como fallo. Se devuelven al ciclo vaciando {Seccion}:{Opcion}.",
                    omitidas.Count,
                    firma,
                    PaymentsOptions.SectionName,
                    nameof(PaymentsOptions.TaskIdsOmitidos));
            }
            else
            {
                _logger.LogInformation("Ya no hay tareas omitidas por configuración en la cola.");
            }
        }

        _logger.LogInformation(
            "El middleware reporta {Cuantas} tarea(s) pendiente(s){Detalle}.",
            tareas.Count,
            omitidas.Count == 0
                ? ""
                : $", de las cuales {omitidas.Count} omitida(s) por configuración");

        return aProcesar;
    }

    /// <summary>Procesa una tarea. Devuelve true si quedó integrada.</summary>
    private async Task<bool> ProcesarTareaAsync(
        ServiceLayerSession session,
        SapAccountPaymentSyncTask tarea,
        CancellationToken cancellationToken)
    {
        var pago = tarea.AccountPayment;

        if (pago is null || string.IsNullOrWhiteSpace(pago.PaymentUuid) ||
            string.IsNullOrWhiteSpace(pago.ClientCode) || string.IsNullOrWhiteSpace(pago.Method))
        {
            return await FallarAsync(
                tarea.TaskId,
                "La tarea no trae un account_payment usable (falta payment_uuid, client_code o method).",
                cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "Tarea {TaskId}: pago {Uuid}, cliente {Cliente}, método {Metodo}, monto {Monto}, " +
            "aplicaciones {Apps}, sin aplicar {SinAplicar}, intentos previos {Intentos}.",
            tarea.TaskId,
            pago.PaymentUuid,
            pago.ClientCode,
            pago.Method,
            pago.Amount,
            pago.ConfirmedApplications?.Count ?? 0,
            pago.UnappliedAmount,
            tarea.Attempts);

        // --- Anti-duplicado ---------------------------------------------------
        // Si un ciclo anterior alcanzó a crear el pago en SAP pero murió antes de
        // reportarlo, la tarea sigue pendiente. Sin este chequeo, el reintento
        // crearía un segundo pago por el mismo dinero.
        var yaExiste = await BuscarPagoExistenteAsync(session, pago.PaymentUuid, cancellationToken)
            .ConfigureAwait(false);

        if (yaExiste is not null)
        {
            _logger.LogWarning(
                "Tarea {TaskId}: el pago {Uuid} YA existe en SAP (DocNum {DocNum}) y no está anulado. " +
                "No se crea otro; se reporta como integrado.",
                tarea.TaskId,
                pago.PaymentUuid,
                yaExiste);

            await ReportarAsync(
                tarea.TaskId,
                SapSyncResultReport.Integrado(yaExiste.Value),
                cancellationToken).ConfigureAwait(false);
            return true;
        }

        // --- Validación de montos --------------------------------------------
        var aplicaciones = pago.ConfirmedApplications ?? [];

        if (aplicaciones.Count == 0)
        {
            // Un pago 100% a cuenta es plausible en SAP, pero NO se ha verificado
            // con una escritura real. No se estrena en producción por accidente.
            return await FallarAsync(
                tarea.TaskId,
                "El pago no trae aplicaciones confirmadas (sería 100% a cuenta). Ese caso no se ha " +
                "verificado todavía contra SAP, así que no se integra automáticamente.",
                cancellationToken).ConfigureAwait(false);
        }

        if (aplicaciones.Any(a => a.Amount is null or <= 0))
        {
            return await FallarAsync(
                tarea.TaskId,
                "Alguna aplicación viene sin monto o con monto no positivo. No se adivina un monto.",
                cancellationToken).ConfigureAwait(false);
        }

        var sumaAplicada = aplicaciones.Sum(a => a.Amount!.Value);
        var esperado = sumaAplicada + pago.UnappliedAmount;

        if (esperado != pago.Amount)
        {
            return await FallarAsync(
                tarea.TaskId,
                $"Los montos no cuadran: aplicado {sumaAplicada} + sin aplicar {pago.UnappliedAmount} " +
                $"= {esperado}, pero el pago dice {pago.Amount}. No se integra con una diferencia " +
                "de dinero sin explicar.",
                cancellationToken).ConfigureAwait(false);
        }

        // --- Resolver los DocEntry -------------------------------------------
        var lineas = new List<IncomingPaymentInvoiceLine>(aplicaciones.Count);
        var problemas = new List<string>();

        foreach (var app in aplicaciones)
        {
            if (!int.TryParse(app.InvoiceDocNum, out var docNum) || docNum <= 0)
            {
                problemas.Add($"invoice_doc_num '{app.InvoiceDocNum}' no es un entero positivo");
                continue;
            }

            var factura = await _resolver
                .LookupInvoiceAsync(pago.ClientCode, docNum, cancellationToken)
                .ConfigureAwait(false);

            switch (factura.Outcome)
            {
                case InvoiceLookupOutcome.Resolved:
                    if (app.Amount!.Value > factura.OpenAmount!.Value)
                    {
                        problemas.Add(
                            $"factura {docNum}: se quiere aplicar {app.Amount} pero su saldo es " +
                            $"{factura.OpenAmount}");
                        break;
                    }

                    lineas.Add(new IncomingPaymentInvoiceLine
                    {
                        DocEntry = factura.DocEntry!.Value,
                        SumApplied = app.Amount!.Value,
                    });
                    break;

                case InvoiceLookupOutcome.NotFound:
                    problemas.Add($"factura {docNum}: no existe en SAP para el cliente {pago.ClientCode}");
                    break;

                case InvoiceLookupOutcome.Closed:
                    problemas.Add(
                        $"factura {docNum} (DocEntry {factura.DocEntry}): ya está cerrada. El pago no " +
                        "existe en SAP con este payment_uuid, así que la factura se saldó por otra vía");
                    break;

                case InvoiceLookupOutcome.Canceled:
                    problemas.Add($"factura {docNum} (DocEntry {factura.DocEntry}): está ANULADA en SAP");
                    break;
            }
        }

        if (problemas.Count > 0)
        {
            // Todo o nada: aplicar solo parte de lo aprobado dejaría el pago
            // divergiendo de lo que autorizó el aprobador.
            return await FallarAsync(
                tarea.TaskId,
                "No se integra por problemas con las facturas: " + string.Join("; ", problemas),
                cancellationToken).ConfigureAwait(false);
        }

        // --- Armar el payload -------------------------------------------------
        IncomingPaymentPayload payload;
        try
        {
            payload = ArmarPayload(pago, lineas);
        }
        catch (InvalidOperationException ex)
        {
            return await FallarAsync(tarea.TaskId, ex.Message, cancellationToken).ConfigureAwait(false);
        }

        var json = payload.ToJson();
        _logger.LogInformation("Tarea {TaskId}: enviando a SAP\n{Json}", tarea.TaskId, json);

        // --- POST a Service Layer --------------------------------------------
        var (status, body) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "IncomingPayments")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (status is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            return await FallarAsync(
                tarea.TaskId,
                $"SAP rechazó el pago ({(int)status}). Respuesta: {body}",
                cancellationToken).ConfigureAwait(false);
        }

        var docNumCreado = LeerDocNum(body);

        if (docNumCreado is null)
        {
            // Caso incómodo: SAP aceptó pero no se pudo leer el DocNum. NO se
            // reporta ERROR, porque el pago sí existe y un reintento lo
            // duplicaría; el anti-duplicado del próximo ciclo lo resolverá.
            _logger.LogError(
                "Tarea {TaskId}: SAP respondió {Codigo} pero no se pudo leer el DocNum. El pago " +
                "existe. No se reporta nada para no marcarlo como error; el chequeo anti-duplicado " +
                "del próximo ciclo lo resolverá. Respuesta: {Body}",
                tarea.TaskId,
                (int)status,
                body);
            return false;
        }

        _logger.LogInformation(
            "Tarea {TaskId}: pago creado en SAP con DocNum {DocNum}.", tarea.TaskId, docNumCreado);

        await ReportarAsync(
            tarea.TaskId,
            SapSyncResultReport.Integrado(docNumCreado.Value),
            cancellationToken).ConfigureAwait(false);

        return true;
    }

    private IncomingPaymentPayload ArmarPayload(
        SapAccountPaymentSnapshot pago,
        List<IncomingPaymentInvoiceLine> lineas)
    {
        // La cuenta que resolvió el middleware manda sobre la configuración local:
        // el usuario eligió el canal / la cuenta al registrar el pago.
        var cuenta = !string.IsNullOrWhiteSpace(pago.ResolvedAccountCode)
            ? pago.ResolvedAccountCode
            : _paymentsOptions.RequireAccountFor(pago.Method!);

        var fecha = string.IsNullOrWhiteSpace(pago.PaymentDate)
            ? throw new InvalidOperationException("El pago no trae payment_date.")
            : pago.PaymentDate!.Length >= 10 ? pago.PaymentDate[..10] : pago.PaymentDate;

        var payload = new IncomingPaymentPayload
        {
            CardCode = pago.ClientCode!,
            DocDate = fecha,
            Remarks = IncomingPaymentPayload.BuildRemarks(
                pago.PaymentUuid!,
                $"{pago.Method} {pago.PaymentChannel}".Trim()),
            PaymentInvoices = lineas,
        };

        switch (pago.Method!.ToUpperInvariant())
        {
            case "EFECTIVO":
                payload.CashAccount = cuenta;
                payload.CashSum = pago.Amount;
                break;

            case "TRANSFERENCIA":
                payload.TransferAccount = cuenta;
                payload.TransferSum = pago.Amount;
                payload.TransferDate = fecha;
                payload.TransferReference = _paymentsOptions.TransferReference;
                break;

            case "CHEQUE":
                if (!int.TryParse(pago.CheckNumber, out var numeroCheque) || numeroCheque <= 0)
                {
                    throw new InvalidOperationException(
                        $"CHEQUE con check_number '{pago.CheckNumber}' inválido: SAP lo exige entero " +
                        "positivo.");
                }

                if (string.IsNullOrWhiteSpace(pago.BankCode))
                {
                    throw new InvalidOperationException(
                        "CHEQUE sin bank_code. SAP lo exige y debe existir en su entidad Banks.");
                }

                payload.CheckAccount = cuenta;
                payload.PaymentChecks =
                [
                    new IncomingPaymentCheckLine
                    {
                        CheckNumber = numeroCheque,
                        BankCode = pago.BankCode!,
                        DueDate = fecha,
                        CheckSum = pago.Amount,
                    },
                ];
                break;

            default:
                throw new InvalidOperationException(
                    $"Método de pago no soportado: '{pago.Method}'. " +
                    "Esperados: EFECTIVO, TRANSFERENCIA, CHEQUE.");
        }

        return payload;
    }

    /// <summary>
    /// Busca en SAP un pago no anulado cuyo <c>Remarks</c> tenga este
    /// <c>payment_uuid</c>. Devuelve su <c>DocNum</c>, o null si no existe.
    /// </summary>
    private async Task<int?> BuscarPagoExistenteAsync(
        ServiceLayerSession session,
        string paymentUuid,
        CancellationToken cancellationToken)
    {
        // Se excluyen los anulados a propósito: un pago que se anuló debe poder
        // reintegrarse. Verificado que este filtro funciona en Service Layer.
        var ruta =
            $"IncomingPayments?$filter=substringof('{Uri.EscapeDataString(paymentUuid)}', Remarks) " +
            "and Cancelled eq 'tNO'&$select=DocEntry,DocNum&$top=1";

        var (status, body) = await session
            .SendForStringAsync(() => new HttpRequestMessage(HttpMethod.Get, ruta), cancellationToken)
            .ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            // Si no se puede verificar, es más seguro abortar que arriesgar un
            // duplicado.
            throw new ServiceLayerException(
                $"No se pudo verificar si el pago {paymentUuid} ya existe en SAP " +
                $"({(int)status}). Respuesta: {body}",
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

    private static int? LeerDocNum(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("DocNum", out var v) && v.TryGetInt32(out var n)
                ? n
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<bool> FallarAsync(int taskId, string detalle, CancellationToken cancellationToken)
    {
        _logger.LogError("Tarea {TaskId} NO integrada: {Detalle}", taskId, detalle);
        await ReportarAsync(taskId, SapSyncResultReport.Error(detalle), cancellationToken)
            .ConfigureAwait(false);
        return false;
    }

    private async Task ReportarAsync(
        int taskId,
        SapSyncResultReport reporte,
        CancellationToken cancellationToken)
    {
        var ruta = $"admin/sap-sync/account-payments/{taskId}/result";

        var (status, body) = await _middleware
            .PostJsonAsync(ruta, reporte.ToJson(), cancellationToken)
            .ConfigureAwait(false);

        if (status == HttpStatusCode.OK)
        {
            // El cuerpo trae la tarea ya actualizada. Se registra completo a
            // propósito: es la única evidencia desde este lado de que el
            // middleware aplicó el cambio, y no hay endpoint de lectura para
            // consultar el AccountPayment después.
            _logger.LogInformation(
                "Tarea {TaskId}: resultado reportado como {Estado}{Ref}. " +
                "Respuesta del middleware:\n{Body}",
                taskId,
                reporte.Status,
                reporte.SapReference is null ? "" : $" (sap_reference {reporte.SapReference})",
                string.IsNullOrWhiteSpace(body) ? "(cuerpo vacío)" : body);
            return;
        }

        // Un reporte que falla es serio: el pago puede estar en SAP y el
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
