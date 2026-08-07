using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.ServiceLayer.CreditNotes;
using DinasWms.SapSync.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Integra UNA tarea real de la cola <c>credit-requests</c>: arma la nota de
/// crédito, la crea en SAP y reporta el resultado.
/// </summary>
/// <remarks>
/// ⚠ En modo real ESCRIBE EN SAP Y ES IRREVERSIBLE: una nota de crédito se
/// ANULA, no se borra, y devuelve inventario.
///
/// Por eso tiene un modo intermedio que las facturas no tuvieron:
/// <c>--Probe:DraftOnly=true</c> arma <b>el mismo payload</b> y lo manda a
/// <c>/Drafts</c>, lo relee y lo borra. Es un ensayo completo contra SAP —
/// incluida la resolución del DocEntry y la validación de montos— sin asentar
/// nada. Conviene correrlo siempre antes del real.
///
/// Uso:
///   --RunMode=CreditNoteProbe --Probe:TaskId=20 --Probe:DraftOnly=true --Probe:Confirm=true
///   --RunMode=CreditNoteProbe --Probe:TaskId=20 --Probe:Confirm=true      (REAL)
/// </remarks>
public sealed class CreditNoteProbeWorker : BackgroundService
{
    /// <summary>Tipo de documento base: factura de deudores (<c>oInvoices</c>).</summary>
    private const int BaseTypeInvoice = 13;

    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly IMiddlewareClient _middleware;
    private readonly IDocEntryResolver _resolver;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<CreditNoteProbeWorker> _logger;

    public CreditNoteProbeWorker(
        IServiceLayerSessionFactory sessionFactory,
        IMiddlewareClient middleware,
        IDocEntryResolver resolver,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<CreditNoteProbeWorker> logger)
    {
        _sessionFactory = sessionFactory;
        _middleware = middleware;
        _resolver = resolver;
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
            _logger.LogError(ex, "PRUEBA DE NOTA DE CRÉDITO FALLIDA. {Message}", ex.Message);
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task CorrerAsync(CancellationToken cancellationToken)
    {
        var soloBorrador = Bandera("Probe:DraftOnly");
        var confirmado = Bandera("Probe:Confirm");
        var omitirReporte = Bandera("Probe:SkipReport");

        if (!int.TryParse(_configuration["Probe:TaskId"], out var taskId) || taskId <= 0)
        {
            Environment.ExitCode = 1;
            _logger.LogError("Falta Probe:TaskId. Ej: --Probe:TaskId=20");
            return;
        }

        // --- 1. La tarea real -------------------------------------------------
        await _middleware.LoginAsync(cancellationToken).ConfigureAwait(false);

        var tarea = await BuscarTareaAsync(taskId, cancellationToken).ConfigureAwait(false);

        if (tarea?.CreditRequest is null)
        {
            Environment.ExitCode = 1;
            _logger.LogError("La tarea {TaskId} no está pendiente o no trae credit_request.", taskId);
            return;
        }

        var solicitud = tarea.CreditRequest;
        var lineas = solicitud.Lines ?? [];
        var docKind = (tarea.DocKind ?? "").ToUpperInvariant();

        var problemas = ValidarAsync(tarea, solicitud, lineas, docKind);

        if (problemas.Count > 0)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "La tarea {TaskId} NO se puede integrar:\n  - {Problemas}\n" +
                "No se reporta nada al middleware desde este arnés: qué hacer con la tarea lo " +
                "decide un humano.",
                taskId,
                string.Join("\n  - ", problemas));
            return;
        }

        // --- 2. DocEntry de la factura, si la hay -----------------------------
        // baseEntry = la factura existe y se puede leer.
        // sePuedeLigar = además está ABIERTA, que es lo único que SAP admite como
        // documento base. Una factura pagada (bost_Close) se rechaza con
        // "One of the base documents has already been closed", aunque sus líneas
        // sigan con RemainingOpenQuantity: SAP mira la cabecera, no la línea.
        int? baseEntry = null;
        var sePuedeLigar = false;

        if (!string.IsNullOrWhiteSpace(solicitud.InvoiceDocNum))
        {
            if (!int.TryParse(solicitud.InvoiceDocNum, out var docNum) || docNum <= 0)
            {
                Environment.ExitCode = 1;
                _logger.LogError(
                    "invoice_doc_num '{Valor}' no es un entero positivo.", solicitud.InvoiceDocNum);
                return;
            }

            var factura = await _resolver
                .LookupInvoiceAsync(solicitud.ClientCode!, docNum, cancellationToken)
                .ConfigureAwait(false);

            switch (factura.Outcome)
            {
                case InvoiceLookupOutcome.Resolved:
                    baseEntry = factura.DocEntry;
                    sePuedeLigar = true;
                    _logger.LogInformation(
                        "Factura {DocNum} del cliente {Cliente} → DocEntry {DocEntry}, ABIERTA. " +
                        "La nota va LIGADA a sus líneas.",
                        docNum,
                        solicitud.ClientCode,
                        factura.DocEntry);
                    break;

                // Pagada: la nota de crédito sigue siendo válida —devolver
                // mercancía no depende de que el cliente haya pagado— pero SAP no
                // deja copiarla desde una factura cerrada. Va INDEPENDIENTE, y el
                // invoice_doc_num queda en Comments para no perder el rastro.
                case InvoiceLookupOutcome.Closed:
                    baseEntry = factura.DocEntry;
                    _logger.LogWarning(
                        "Factura {DocNum} del cliente {Cliente} → DocEntry {DocEntry}, PAGADA " +
                        "(bost_Close). SAP no admite ligar contra una factura cerrada, así que la " +
                        "nota va INDEPENDIENTE y la referencia queda en Comments.",
                        docNum,
                        solicitud.ClientCode,
                        factura.DocEntry);
                    break;

                case InvoiceLookupOutcome.NotFound:
                    Environment.ExitCode = 1;
                    _logger.LogError(
                        "La factura {DocNum} NO existe para el cliente {Cliente}. La solicitud " +
                        "referencia un documento que SAP no reconoce; no se inventa una nota " +
                        "independiente en su lugar, porque perdería la trazabilidad que el " +
                        "aprobador creyó estar dando.",
                        docNum,
                        solicitud.ClientCode);
                    return;

                case InvoiceLookupOutcome.Canceled:
                    Environment.ExitCode = 1;
                    _logger.LogError(
                        "La factura {DocNum} (DocEntry {DocEntry}) está ANULADA en SAP.",
                        docNum,
                        factura.DocEntry);
                    return;
            }
        }
        else
        {
            _logger.LogInformation("La solicitud no trae invoice_doc_num: nota INDEPENDIENTE.");
        }

        var esItems = docKind == "ITEMS";

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        // --- 3. ¿La línea base existe y es del ítem que dice la solicitud? -----
        // El aprobador elige el base_line viendo la factura, pero entre que lo
        // elige y esto corre puede pasar cualquier cosa. Verificarlo cuesta un
        // GET; equivocarse acredita el artículo equivocado.
        if (esItems && baseEntry is not null)
        {
            var desajustes = await VerificarLineasBaseAsync(
                session, baseEntry.Value, lineas, cancellationToken).ConfigureAwait(false);

            if (desajustes.Count > 0)
            {
                Environment.ExitCode = 1;
                _logger.LogError(
                    "La tarea {TaskId} referencia líneas de factura que no cuadran:\n  - {Desajustes}",
                    taskId,
                    string.Join("\n  - ", desajustes));
                return;
            }
        }

        // --- 4. El payload ----------------------------------------------------

        var payload = new CreditNotePayload
        {
            DocType = esItems ? "dDocument_Items" : "dDocument_Service",
            DocObjectCode = soloBorrador ? "oCreditNotes" : null,
            CardCode = solicitud.ClientCode!,
            DocDate = DateOnly.FromDateTime(solicitud.DecidedAt.LocalDateTime).ToString("yyyy-MM-dd"),
            Comments = CreditNotePayload.BuildComments(
                solicitud.RequestUuid!, docKind, solicitud.InvoiceDocNum, solicitud.Reason),
            DocumentLines = lineas
                .Select(l => ArmarLinea(l, esItems, sePuedeLigar ? baseEntry : null))
                .ToList(),
        };

        var json = payload.ToJson();

        _logger.LogInformation(
            "=== NOTA DE CRÉDITO {Modo} ===\n" +
            "  Tarea {TaskId} | request_uuid {Uuid} | doc_kind {Kind} | motivo {Motivo}\n" +
            "  Cliente {Cliente} | factura {Factura} | calculated_amount {Total}\n{Json}",
            soloBorrador ? "(ENSAYO EN BORRADOR)" : "REAL",
            taskId,
            solicitud.RequestUuid,
            docKind,
            solicitud.Reason,
            solicitud.ClientCode,
            baseEntry is null
                ? "(sin factura, independiente)"
                : $"DocEntry {baseEntry} — {(sePuedeLigar ? "LIGADA" : "independiente, factura pagada")}",
            solicitud.CalculatedAmount.ToString("F2", CultureInfo.InvariantCulture),
            json);

        if (!confirmado)
        {
            _logger.LogWarning(
                "SIMULACIÓN — no se envió nada. {Aviso}",
                soloBorrador
                    ? "Hace falta --Probe:Confirm=true (crea un borrador, que sí se borra)."
                    : "Esta nota es REAL e IRREVERSIBLE. Hace falta --Probe:Confirm=true.");
            return;
        }

        // --- 5. Anti-duplicado ------------------------------------------------
        // Se busca el par uuid + doc_kind, no solo el uuid: una solicitud puede
        // producir DOS notas, y buscar solo por uuid encontraría la hermana y
        // daría por integrada una que nunca se creó.
        var marca = $"request_uuid={solicitud.RequestUuid} | doc_kind={docKind}";
        var yaExiste = await BuscarNotaExistenteAsync(session, marca, cancellationToken)
            .ConfigureAwait(false);

        if (yaExiste is not null)
        {
            _logger.LogWarning(
                "ANTI-DUPLICADO: ya existe la nota {DocNum} para {Marca}, no anulada. No se crea otra.",
                yaExiste,
                marca);

            if (!soloBorrador && !omitirReporte)
            {
                await ReportarAsync(
                    taskId, SapSyncResultReport.Integrado(yaExiste.Value), cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }

        _logger.LogInformation("ANTI-DUPLICADO: no hay ninguna nota no anulada para {Marca}.", marca);

        // --- 5. Crear ---------------------------------------------------------
        var ruta = soloBorrador ? "Drafts" : "CreditNotes";

        if (!soloBorrador)
        {
            _logger.LogWarning("Enviando POST /CreditNotes. ESTO ES REAL E IRREVERSIBLE…");
        }

        var (status, body) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Post, ruta)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                },
                cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "=== RESPUESTA LITERAL DE SAP ({Codigo} {Status}), {Bytes:N0} bytes ===\n{Body}",
            (int)status,
            status,
            body.Length,
            body);

        if (status is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "=== SAP RECHAZÓ el documento ({Codigo}). La tarea {TaskId} queda como estaba. ===",
                (int)status,
                taskId);
            return;
        }

        var docEntry = LeerEntero(body, "DocEntry");
        var docNumCreado = LeerEntero(body, "DocNum");
        var docTotal = LeerDecimal(body, "DocTotal");

        _logger.LogInformation(
            "=== {Que} CREADO: DocNum {DocNum}, DocEntry {DocEntry} ===",
            soloBorrador ? "BORRADOR" : "NOTA DE CRÉDITO",
            docNumCreado,
            docEntry);

        // --- 6. El total contra lo aprobado -----------------------------------
        if (docTotal is null)
        {
            Environment.ExitCode = 1;
            _logger.LogError("No se pudo leer el DocTotal de la respuesta.");
        }
        else if (docTotal.Value == solicitud.CalculatedAmount)
        {
            _logger.LogInformation(
                "DocTotal: SAP {Real} == calculated_amount {Esperado}. COINCIDE.",
                docTotal.Value.ToString("F2", CultureInfo.InvariantCulture),
                solicitud.CalculatedAmount.ToString("F2", CultureInfo.InvariantCulture));
        }
        else
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "DocTotal: SAP {Real} != calculated_amount {Esperado}. DIFERENCIA de {Dif}.",
                docTotal.Value.ToString("F2", CultureInfo.InvariantCulture),
                solicitud.CalculatedAmount.ToString("F2", CultureInfo.InvariantCulture),
                (docTotal.Value - solicitud.CalculatedAmount).ToString("F2", CultureInfo.InvariantCulture));
        }

        // --- 7. Borrador: releer y borrar -------------------------------------
        if (soloBorrador)
        {
            if (docEntry is null)
            {
                Environment.ExitCode = 1;
                _logger.LogError("No se pudo leer el DocEntry del borrador: HAY QUE BORRARLO A MANO.");
                return;
            }

            await BorrarBorradorAsync(session, docEntry.Value, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "=== ENSAYO TERMINADO. El payload es válido para SAP y no quedó nada asentado. ===");
            return;
        }

        // --- 8. Cerrar el ciclo -----------------------------------------------
        if (docNumCreado is null)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "SAP aceptó la nota pero no se pudo leer su DocNum: NO se reporta nada (reportar " +
                "ERROR sería mentir y reintentar la duplicaría). El anti-duplicado del próximo " +
                "ciclo lo resuelve.");
            return;
        }

        if (omitirReporte)
        {
            _logger.LogWarning("--Probe:SkipReport=true — no se reporta al middleware.");
            return;
        }

        await ReportarAsync(taskId, SapSyncResultReport.Integrado(docNumCreado.Value), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Todo lo que se puede rechazar ANTES de tocar SAP. Ante la duda, no se
    /// postea: una nota mal creada mueve inventario y hay que anularla a mano.
    /// </summary>
    private List<string> ValidarAsync(
        SapCreditRequestSyncTask tarea,
        SapCreditRequestSnapshot solicitud,
        List<SapCreditNoteLine> lineas,
        string docKind)
    {
        var problemas = new List<string>();

        if (docKind is not ("ITEMS" or "SERVICE"))
        {
            problemas.Add($"doc_kind '{tarea.DocKind}' desconocido (esperados: ITEMS, SERVICE)");
        }

        if (string.IsNullOrWhiteSpace(solicitud.RequestUuid))
        {
            problemas.Add("la solicitud no trae request_uuid, y sin él no hay anti-duplicado");
        }

        if (string.IsNullOrWhiteSpace(solicitud.ClientCode))
        {
            problemas.Add("la solicitud no trae client_code");
        }

        if (lineas.Count == 0)
        {
            problemas.Add("la solicitud no trae líneas");
        }

        var esItems = docKind == "ITEMS";

        // Motivos válidos en cada tipo de documento. El reason de la línea es
        // SOLO para verificar: la cuenta y el almacén ya vienen resueltos y no se
        // derivan de él. Acá únicamente se comprueba coherencia, y si no cuadra
        // NO se postea — un motivo mal leído del otro lado habría resuelto
        // también la cuenta y el almacén del motivo equivocado.
        var motivosValidos = esItems
            ? new[] { "DOESNT_WANT_IT", "MISTAKE", "DAMAGED" }
            : ["SHORT"];

        for (var i = 0; i < lineas.Count; i++)
        {
            var l = lineas[i];

            if (string.IsNullOrWhiteSpace(l.Reason))
            {
                // Sin reason no hay nada que verificar. No se bloquea —el campo
                // es nuevo— pero se dice, porque la defensa en profundidad que
                // pidió el contrato queda inactiva para esa línea.
                _logger.LogWarning(
                    "Línea {Linea} sin reason: no se puede verificar su coherencia con doc_kind " +
                    "{Kind}. El contrato lo exige desde v0.32.0.",
                    i,
                    docKind);
            }
            else if (!motivosValidos.Contains(l.Reason, StringComparer.OrdinalIgnoreCase))
            {
                problemas.Add(
                    $"línea {i}: motivo '{l.Reason}' no corresponde a un documento {docKind} " +
                    $"(esperados: {string.Join(", ", motivosValidos)})");
            }

            if (l.ApprovedAmount <= 0)
            {
                problemas.Add($"línea {i}: approved_amount {l.ApprovedAmount} no es positivo");
            }

            if (string.IsNullOrWhiteSpace(l.AccountCode))
            {
                problemas.Add($"línea {i}: sin account_code; no se adivina una cuenta contable");
            }

            if (!esItems)
            {
                continue;
            }

            if (l.Quantity <= 0)
            {
                problemas.Add($"línea {i}: cantidad {l.Quantity} no positiva en una nota de ítems");
                continue;
            }

            if (string.IsNullOrWhiteSpace(l.ItemCode))
            {
                problemas.Add($"línea {i}: sin item_code en una nota de ítems");
            }

            if (string.IsNullOrWhiteSpace(l.WarehouseCode))
            {
                problemas.Add($"línea {i}: sin warehouse_code; el stock tiene que volver a algún lado");
            }

            // El precio unitario sale de dividir el monto aprobado entre la
            // cantidad. Si esa división no vuelve exacta, el LineTotal de SAP no
            // va a ser el monto aprobado, y una nota de crédito que no acredita
            // lo aprobado es un problema contable.
            var precio = decimal.Round(l.ApprovedAmount / l.Quantity, 6, MidpointRounding.AwayFromZero);

            if (precio * l.Quantity != l.ApprovedAmount)
            {
                problemas.Add(
                    $"línea {i}: approved_amount {l.ApprovedAmount} entre {l.Quantity} no da un " +
                    $"precio exacto ({precio}); el total quedaría distinto de lo aprobado");
            }
        }

        // El total del documento tiene que ser lo que se aprobó.
        var suma = lineas.Sum(l => l.ApprovedAmount);

        if (suma != solicitud.CalculatedAmount)
        {
            problemas.Add(
                $"las líneas suman {suma} pero calculated_amount dice " +
                $"{solicitud.CalculatedAmount}");
        }

        return problemas;
    }

    private CreditNoteLinePayload ArmarLinea(SapCreditNoteLine l, bool esItems, int? baseEntry)
    {
        if (!esItems)
        {
            // En servicio la cantidad NO multiplica (verificado): el monto ES el
            // precio. Se manda Quantity 0 como hacen las notas reales de la base.
            // El base_line que venga se ignora: una línea de servicio no se puede
            // ligar a una factura.
            if (l.BaseLine is not null)
            {
                _logger.LogWarning(
                    "Línea de SERVICIO con base_line {BaseLine}: se IGNORA. Una línea de servicio no " +
                    "se liga a una factura; el vínculo solo existe en las de ítems.",
                    l.BaseLine);
            }

            return new CreditNoteLinePayload
            {
                ItemDescription = string.IsNullOrWhiteSpace(l.ItemCode) ? "Credito" : l.ItemCode,
                Quantity = 0m,
                UnitPrice = l.ApprovedAmount,
                AccountCode = l.AccountCode,
                TaxCode = "Exempt",
            };
        }

        var linea = new CreditNoteLinePayload
        {
            Quantity = l.Quantity,
            UnitPrice = decimal.Round(l.ApprovedAmount / l.Quantity, 6, MidpointRounding.AwayFromZero),
            AccountCode = l.AccountCode,
            WarehouseCode = l.WarehouseCode,
        };

        if (baseEntry is not null && l.BaseLine is not null)
        {
            // Ligada: SAP copia ItemCode y TaxCode de la línea base. Mandar el
            // ItemCode también sería redundante y arriesga contradecir la base.
            linea.BaseType = BaseTypeInvoice;
            linea.BaseEntry = baseEntry;
            linea.BaseLine = l.BaseLine;
        }
        else
        {
            linea.ItemCode = l.ItemCode;
            linea.TaxCode = "Exempt";
        }

        return linea;
    }

    /// <summary>
    /// Comprueba, contra la factura real, que cada <c>base_line</c> exista y
    /// corresponda al <c>item_code</c> que dice la solicitud.
    /// </summary>
    private async Task<List<string>> VerificarLineasBaseAsync(
        ServiceLayerSession session,
        int baseEntry,
        List<SapCreditNoteLine> lineas,
        CancellationToken cancellationToken)
    {
        var desajustes = new List<string>();

        var (status, body) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Get, $"Invoices({baseEntry})?$select=DocEntry,DocNum,DocumentLines"),
                cancellationToken)
            .ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            throw new ServiceLayerException(
                $"No se pudieron leer las líneas de la factura DocEntry {baseEntry} ({(int)status}).",
                status,
                body);
        }

        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("DocumentLines", out var lineasFactura) ||
            lineasFactura.ValueKind != JsonValueKind.Array)
        {
            desajustes.Add($"la factura DocEntry {baseEntry} no devolvió líneas");
            return desajustes;
        }

        var porNumero = new Dictionary<int, (string Item, decimal Cantidad)>();

        foreach (var lf in lineasFactura.EnumerateArray())
        {
            if (lf.TryGetProperty("LineNum", out var num) && num.TryGetInt32(out var n))
            {
                porNumero[n] = (
                    lf.TryGetProperty("ItemCode", out var it) ? it.GetString() ?? "" : "",
                    lf.TryGetProperty("Quantity", out var q) && q.ValueKind == JsonValueKind.Number
                        ? q.GetDecimal()
                        : 0m);
            }
        }

        for (var i = 0; i < lineas.Count; i++)
        {
            var l = lineas[i];

            if (l.BaseLine is null)
            {
                continue;
            }

            if (!porNumero.TryGetValue(l.BaseLine.Value, out var baseLinea))
            {
                desajustes.Add(
                    $"línea {i}: base_line {l.BaseLine} no existe en la factura (tiene líneas " +
                    $"{string.Join(", ", porNumero.Keys.Order())})");
                continue;
            }

            if (!string.Equals(baseLinea.Item, l.ItemCode, StringComparison.OrdinalIgnoreCase))
            {
                desajustes.Add(
                    $"línea {i}: la solicitud dice '{l.ItemCode}' pero la línea {l.BaseLine} de la " +
                    $"factura es '{baseLinea.Item}'");
            }

            if (l.Quantity > baseLinea.Cantidad)
            {
                // El Dashboard valida esto al aprobar; acá es la última red. SAP
                // NO lo valida: se verificó que acepta acreditar 10 sobre una
                // línea de 1, sin advertir.
                desajustes.Add(
                    $"línea {i}: se acreditan {l.Quantity} pero la línea {l.BaseLine} de la factura " +
                    $"solo tiene {baseLinea.Cantidad}");
            }
        }

        return desajustes;
    }

    private async Task<SapCreditRequestSyncTask?> BuscarTareaAsync(
        int taskId,
        CancellationToken cancellationToken)
    {
        const string ruta = "admin/sap-sync/credit-requests/pending";

        var (status, body) = await _middleware.GetAsync(ruta, cancellationToken).ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            throw new MiddlewareException(
                $"El middleware devolvió {(int)status} al pedir la cola de créditos.", status, body);
        }

        var pagina = JsonSerializer.Deserialize<SapCreditRequestSyncTasksPage>(body, SapSyncJson.Options);
        return pagina?.Tasks?.FirstOrDefault(t => t.TaskId == taskId);
    }

    private async Task<int?> BuscarNotaExistenteAsync(
        ServiceLayerSession session,
        string marca,
        CancellationToken cancellationToken)
    {
        var ruta =
            $"CreditNotes?$filter=substringof('{Uri.EscapeDataString(marca)}', Comments) " +
            "and Cancelled eq 'tNO'&$select=DocEntry,DocNum&$top=1";

        var (status, body) = await session
            .SendForStringAsync(() => new HttpRequestMessage(HttpMethod.Get, ruta), cancellationToken)
            .ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            throw new ServiceLayerException(
                $"No se pudo verificar si la nota de {marca} ya existe ({(int)status}). {body}",
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

    private async Task BorrarBorradorAsync(
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
            Environment.ExitCode = 1;
            _logger.LogError(
                "FALLÓ el borrado del borrador {DocEntry} ({Codigo}). HAY QUE BORRARLO A MANO: {Body}",
                docEntry,
                (int)status,
                body);
            return;
        }

        var (statusVerif, _) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Get, $"Drafts({docEntry})"),
                cancellationToken)
            .ConfigureAwait(false);

        if (statusVerif == HttpStatusCode.NotFound)
        {
            _logger.LogInformation("Borrador {DocEntry} borrado y verificado (404).", docEntry);
        }
        else
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "El DELETE respondió bien pero el borrador {DocEntry} sigue respondiendo ({Codigo}).",
                docEntry,
                (int)statusVerif);
        }
    }

    private async Task ReportarAsync(
        int taskId,
        SapSyncResultReport reporte,
        CancellationToken cancellationToken)
    {
        var ruta = $"admin/sap-sync/credit-requests/{taskId}/result";

        var (status, body) = await _middleware
            .PostJsonAsync(ruta, reporte.ToJson(), cancellationToken)
            .ConfigureAwait(false);

        if (status == HttpStatusCode.OK)
        {
            _logger.LogInformation(
                "=== CICLO CERRADO. Tarea {TaskId} reportada como {Estado}. Respuesta:\n{Body}",
                taskId,
                reporte.Status,
                string.IsNullOrWhiteSpace(body) ? "(cuerpo vacío)" : body);

            // Desde v0.32.0 la tarea devuelve sap_reference. Comprobar que quedó
            // guardado el número correcto es distinto de saber que la llamada no
            // falló: lo segundo ya lo sabíamos, lo primero es lo que importa.
            VerificarReferencia(body, reporte.SapReference);
            return;
        }

        Environment.ExitCode = 1;
        _logger.LogError(
            "FALLÓ el reporte del resultado ({Codigo}). La nota está en SAP y el middleware NO lo " +
            "sabe: {Body}",
            (int)status,
            body);
    }

    /// <summary>
    /// Comprueba que el <c>sap_reference</c> que devolvió el middleware sea el
    /// número que se le reportó.
    /// </summary>
    private void VerificarReferencia(string body, string? reportada)
    {
        SapCreditRequestSyncTask? tarea;

        try
        {
            tarea = JsonSerializer.Deserialize<SapCreditRequestSyncTask>(body, SapSyncJson.Options);
        }
        catch (JsonException)
        {
            _logger.LogWarning(
                "No se pudo interpretar la respuesta del reporte, así que no se pudo comprobar el " +
                "sap_reference.");
            return;
        }

        if (string.IsNullOrWhiteSpace(tarea?.SapReference))
        {
            _logger.LogWarning(
                "El middleware no devolvió sap_reference. La tarea quedó en {Estado}, pero desde " +
                "acá no se puede confirmar que el número {Reportada} se haya guardado.",
                tarea?.Status,
                reportada);
            return;
        }

        if (string.Equals(tarea.SapReference, reportada, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "sap_reference CONFIRMADO: el middleware guardó {Referencia}.", tarea.SapReference);
        }
        else
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "sap_reference NO COINCIDE: se reportó {Reportada} y el middleware guardó " +
                "{Guardada}. El documento de SAP y lo que dice el middleware apuntan a números " +
                "distintos.",
                reportada,
                tarea.SapReference);
        }
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
