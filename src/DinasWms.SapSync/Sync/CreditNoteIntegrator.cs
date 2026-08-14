using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.ServiceLayer.CreditNotes;
using DinasWms.SapSync.Sql;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Sync;

/// <summary>
/// Convierte UNA tarea de <c>credit-requests</c> en una nota de crédito de SAP.
/// </summary>
/// <remarks>
/// Mismo papel que <see cref="OrderInvoiceIntegrator"/> y por la misma razón: que
/// el arnés manual (<c>--RunMode=CreditNoteProbe</c>) y el día que exista un paso
/// automático corran <b>exactamente</b> la misma lógica. Acá lo que no puede
/// divergir es más delicado que en las facturas: el anti-duplicado por
/// <c>uuid + doc_kind</c>, la decisión de ligar o no contra la factura base, y la
/// verificación de que el <c>base_line</c> sea del ítem que dice la solicitud.
/// Equivocarse acredita el artículo equivocado y mueve inventario.
///
/// <para>
/// ⚠ En modo real ESCRIBE EN SAP Y ES IRREVERSIBLE: una nota de crédito se ANULA,
/// no se borra. Por eso conserva el ensayo en borrador (<paramref name="soloBorrador"/>),
/// que arma el MISMO payload, lo manda a <c>/Drafts</c>, lo relee y lo borra.
/// </para>
/// <para>
/// No abre sesión ni reporta al middleware: las dos cosas son del llamador. El
/// arnés a veces no debe reportar —una prueba no decide el estado de una tarea— y
/// la sesión la administra el ciclo.
/// </para>
/// <para>
/// Tampoco toca <c>Environment.ExitCode</c>. Qué es un fallo del proceso es
/// política del arnés, no del integrador: corriendo dentro de un ciclo, una nota
/// rechazada es una tarea que se reporta con error, no un worker que se cae.
/// </para>
/// </remarks>
public sealed class CreditNoteIntegrator
{
    /// <summary>Tipo de documento base: factura de deudores (<c>oInvoices</c>).</summary>
    private const int BaseTypeInvoice = 13;

    private readonly IDocEntryResolver _resolver;
    private readonly ILogger<CreditNoteIntegrator> _logger;

    public CreditNoteIntegrator(
        IDocEntryResolver resolver,
        ILogger<CreditNoteIntegrator> logger)
    {
        _resolver = resolver;
        _logger = logger;
    }

    /// <param name="simular">
    /// Hace todo menos escribir: valida, resuelve el DocEntry, verifica las líneas
    /// base y muestra el payload. Es lo que usa el arnés para mirar qué se
    /// enviaría sin tocar SAP.
    /// </param>
    /// <param name="soloBorrador">
    /// Manda el payload a <c>/Drafts</c> en vez de <c>/CreditNotes</c>, lo verifica
    /// y lo borra. Ensayo completo contra SAP sin asentar nada.
    /// </param>
    public async Task<CreditNoteOutcome> IntegrarAsync(
        ServiceLayerSession session,
        SapCreditRequestSyncTask tarea,
        CancellationToken cancellationToken,
        bool simular = false,
        bool soloBorrador = false)
    {
        var solicitud = tarea.CreditRequest;

        if (solicitud is null)
        {
            return CreditNoteOutcome.Rechazada("la tarea no trae credit_request");
        }

        var lineas = solicitud.Lines ?? [];
        var docKind = (tarea.DocKind ?? "").ToUpperInvariant();

        var problemas = Validar(tarea, solicitud, lineas, docKind);

        if (problemas.Count > 0)
        {
            return CreditNoteOutcome.Rechazada(string.Join("; ", problemas));
        }

        // --- DocEntry de la factura, si la hay ---------------------------------
        var (resuelto, rechazo) = await ResolverFacturaBaseAsync(solicitud, cancellationToken)
            .ConfigureAwait(false);

        if (rechazo is not null)
        {
            return CreditNoteOutcome.Rechazada(rechazo);
        }

        var (baseEntry, sePuedeLigar) = resuelto;
        var esItems = docKind == "ITEMS";

        // --- ¿La línea base existe y es del ítem que dice la solicitud? --------
        // El aprobador elige el base_line viendo la factura, pero entre que lo
        // elige y esto corre puede pasar cualquier cosa. Verificarlo cuesta un
        // GET; equivocarse acredita el artículo equivocado.
        if (esItems && baseEntry is not null)
        {
            var desajustes = await VerificarLineasBaseAsync(
                session, baseEntry.Value, lineas, cancellationToken).ConfigureAwait(false);

            if (desajustes.Count > 0)
            {
                return CreditNoteOutcome.Rechazada(
                    "la solicitud referencia líneas de factura que no cuadran: " +
                    string.Join("; ", desajustes));
            }
        }

        // --- Payload ------------------------------------------------------------
        var payload = ArmarPayload(solicitud, lineas, docKind, esItems, soloBorrador, baseEntry, sePuedeLigar);
        var json = payload.ToJson();

        _logger.LogInformation(
            "Tarea {TaskId} — nota de crédito {Modo}\n" +
            "  request_uuid {Uuid} | doc_kind {Kind} | motivo {Motivo}\n" +
            "  Cliente {Cliente} | factura {Factura} | calculated_amount {Total}\n{Json}",
            tarea.TaskId,
            soloBorrador ? "(ENSAYO EN BORRADOR)" : "REAL",
            solicitud.RequestUuid,
            docKind,
            solicitud.Reason,
            solicitud.ClientCode,
            baseEntry is null
                ? "(sin factura, independiente)"
                : $"DocEntry {baseEntry} — {(sePuedeLigar ? "LIGADA" : "independiente, factura pagada")}",
            solicitud.CalculatedAmount.ToString("F2", CultureInfo.InvariantCulture),
            json);

        if (simular)
        {
            return CreditNoteOutcome.SimuladaOk();
        }

        // --- Anti-duplicado -----------------------------------------------------
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

            return CreditNoteOutcome.YaExistia(yaExiste.Value);
        }

        _logger.LogInformation("ANTI-DUPLICADO: no hay ninguna nota no anulada para {Marca}.", marca);

        // --- Crear --------------------------------------------------------------
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

        // La respuesta literal, completa. No es ruido: en este proyecto lo que se
        // cree saber del contrato de Service Layer se comprueba leyendo lo que SAP
        // contestó de verdad, y una nota de crédito es irreversible — si algo salió
        // distinto de lo esperado, esta línea es la única evidencia de qué asentó.
        _logger.LogInformation(
            "=== RESPUESTA LITERAL DE SAP ({Codigo} {Status}), {Bytes:N0} bytes ===\n{Body}",
            (int)status,
            status,
            body.Length,
            body);

        if (status is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            return CreditNoteOutcome.Rechazada(
                $"SAP rechazó el documento ({(int)status}). {body}");
        }

        var docEntry = LeerEntero(body, "DocEntry");
        var docNum = LeerEntero(body, "DocNum");
        var docTotal = LeerDecimal(body, "DocTotal");

        _logger.LogInformation(
            "{Que} CREADO: DocNum {DocNum}, DocEntry {DocEntry}",
            soloBorrador ? "BORRADOR" : "NOTA DE CRÉDITO",
            docNum,
            docEntry);

        // --- El total contra lo aprobado ---------------------------------------
        var discrepa = ComprobarTotal(docTotal, solicitud.CalculatedAmount);

        // --- Borrador: releer y borrar -----------------------------------------
        if (soloBorrador)
        {
            if (docEntry is null)
            {
                return CreditNoteOutcome.EnsayoTerminado(
                    null,
                    docNum,
                    docTotal,
                    discrepa,
                    "No se pudo leer el DocEntry del borrador: HAY QUE BORRARLO A MANO.");
            }

            var avisoBorrado = await BorrarBorradorAsync(session, docEntry.Value, cancellationToken)
                .ConfigureAwait(false);

            return CreditNoteOutcome.EnsayoTerminado(
                docEntry, docNum, docTotal, discrepa, avisoBorrado);
        }

        if (docNum is null)
        {
            // SAP aceptó pero no se puede leer el DocNum: la nota EXISTE. No es un
            // error reportable — reportar ERROR sería mentir y un reintento la
            // duplicaría. Se deja que el anti-duplicado del próximo ciclo la
            // encuentre.
            _logger.LogError(
                "SAP respondió {Codigo} pero no se pudo leer el DocNum. La nota existe. No se " +
                "reporta nada; el anti-duplicado del próximo ciclo lo resuelve. Respuesta: {Body}",
                (int)status,
                body);

            return CreditNoteOutcome.CreadaSinNumero(docEntry, docTotal, discrepa);
        }

        return CreditNoteOutcome.Creada(docNum.Value, docEntry, docTotal, discrepa);
    }

    /// <summary>
    /// Resuelve el DocEntry de la factura referenciada y decide si la nota puede
    /// ir LIGADA a sus líneas.
    /// </summary>
    /// <remarks>
    /// <c>sePuedeLigar</c> no es lo mismo que "la factura existe": SAP solo admite
    /// como documento base una factura ABIERTA. Una pagada (<c>bost_Close</c>) se
    /// rechaza con "One of the base documents has already been closed" aunque sus
    /// líneas sigan con <c>RemainingOpenQuantity</c> — SAP mira la cabecera, no la
    /// línea.
    /// </remarks>
    private async Task<((int? BaseEntry, bool SePuedeLigar) Resuelto, string? Rechazo)>
        ResolverFacturaBaseAsync(
            SapCreditRequestSnapshot solicitud,
            CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(solicitud.InvoiceDocNum))
        {
            _logger.LogInformation("La solicitud no trae invoice_doc_num: nota INDEPENDIENTE.");
            return ((null, false), null);
        }

        if (!int.TryParse(solicitud.InvoiceDocNum, out var docNum) || docNum <= 0)
        {
            return ((null, false),
                $"invoice_doc_num '{solicitud.InvoiceDocNum}' no es un entero positivo");
        }

        var factura = await _resolver
            .LookupInvoiceAsync(solicitud.ClientCode!, docNum, cancellationToken)
            .ConfigureAwait(false);

        switch (factura.Outcome)
        {
            case InvoiceLookupOutcome.Resolved:
                _logger.LogInformation(
                    "Factura {DocNum} del cliente {Cliente} → DocEntry {DocEntry}, ABIERTA. " +
                    "La nota va LIGADA a sus líneas.",
                    docNum,
                    solicitud.ClientCode,
                    factura.DocEntry);
                return ((factura.DocEntry, true), null);

            // Pagada: la nota de crédito sigue siendo válida —devolver mercancía no
            // depende de que el cliente haya pagado— pero SAP no deja copiarla desde
            // una factura cerrada. Va INDEPENDIENTE, y el invoice_doc_num queda en
            // Comments para no perder el rastro.
            case InvoiceLookupOutcome.Closed:
                _logger.LogWarning(
                    "Factura {DocNum} del cliente {Cliente} → DocEntry {DocEntry}, PAGADA " +
                    "(bost_Close). SAP no admite ligar contra una factura cerrada, así que la nota " +
                    "va INDEPENDIENTE y la referencia queda en Comments.",
                    docNum,
                    solicitud.ClientCode,
                    factura.DocEntry);
                return ((factura.DocEntry, false), null);

            case InvoiceLookupOutcome.NotFound:
                return ((null, false),
                    $"la factura {docNum} NO existe para el cliente {solicitud.ClientCode}; la " +
                    "solicitud referencia un documento que SAP no reconoce, y no se inventa una " +
                    "nota independiente en su lugar porque perdería la trazabilidad que el " +
                    "aprobador creyó estar dando");

            case InvoiceLookupOutcome.Canceled:
                return ((null, false),
                    $"la factura {docNum} (DocEntry {factura.DocEntry}) está ANULADA en SAP");

            default:
                return ((null, false), $"resultado inesperado al buscar la factura {docNum}");
        }
    }

    /// <summary>
    /// Todo lo que se puede rechazar ANTES de tocar SAP. Ante la duda, no se
    /// postea: una nota mal creada mueve inventario y hay que anularla a mano.
    /// </summary>
    private List<string> Validar(
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

        // Motivos válidos en cada tipo de documento. El reason de la línea es SOLO
        // para verificar: la cuenta y el almacén ya vienen resueltos y no se derivan
        // de él. Acá únicamente se comprueba coherencia, y si no cuadra NO se postea
        // — un motivo mal leído del otro lado habría resuelto también la cuenta y el
        // almacén del motivo equivocado.
        var motivosValidos = esItems
            ? new[] { "DOESNT_WANT_IT", "MISTAKE", "DAMAGED" }
            : ["SHORT"];

        for (var i = 0; i < lineas.Count; i++)
        {
            var l = lineas[i];

            if (string.IsNullOrWhiteSpace(l.Reason))
            {
                // Sin reason no hay nada que verificar. No se bloquea —el campo es
                // nuevo— pero se dice, porque la defensa en profundidad que pidió el
                // contrato queda inactiva para esa línea.
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

            // El precio unitario sale de dividir el monto aprobado entre la cantidad.
            // Si esa división no vuelve exacta, el LineTotal de SAP no va a ser el
            // monto aprobado, y una nota de crédito que no acredita lo aprobado es un
            // problema contable.
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
                $"las líneas suman {suma} pero calculated_amount dice {solicitud.CalculatedAmount}");
        }

        return problemas;
    }

    private CreditNotePayload ArmarPayload(
        SapCreditRequestSnapshot solicitud,
        List<SapCreditNoteLine> lineas,
        string docKind,
        bool esItems,
        bool soloBorrador,
        int? baseEntry,
        bool sePuedeLigar) =>
        new()
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

    private CreditNoteLinePayload ArmarLinea(SapCreditNoteLine l, bool esItems, int? baseEntry)
    {
        if (!esItems)
        {
            // En servicio la cantidad NO multiplica (verificado): el monto ES el
            // precio. Se manda Quantity 0 como hacen las notas reales de la base. El
            // base_line que venga se ignora: una línea de servicio no se puede ligar
            // a una factura.
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
    private static async Task<List<string>> VerificarLineasBaseAsync(
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
                // El Dashboard valida esto al aprobar; acá es la última red. SAP NO lo
                // valida: se verificó que acepta acreditar 10 sobre una línea de 1,
                // sin advertir.
                desajustes.Add(
                    $"línea {i}: se acreditan {l.Quantity} pero la línea {l.BaseLine} de la factura " +
                    $"solo tiene {baseLinea.Cantidad}");
            }
        }

        return desajustes;
    }

    private static async Task<int?> BuscarNotaExistenteAsync(
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
            // No poder verificar es peor que esperar: una nota duplicada acredita dos
            // veces y devuelve el inventario dos veces.
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

    /// <summary>
    /// ¿El total que asentó SAP es lo que se aprobó? Devuelve true si discrepa (o
    /// si no se pudo leer, que a estos efectos es igual de malo: no se puede
    /// afirmar que coincida).
    /// </summary>
    private bool ComprobarTotal(decimal? docTotal, decimal calculatedAmount)
    {
        if (docTotal is null)
        {
            _logger.LogError("No se pudo leer el DocTotal de la respuesta de SAP.");
            return true;
        }

        if (docTotal.Value == calculatedAmount)
        {
            _logger.LogInformation(
                "DocTotal: SAP {Real} == calculated_amount {Esperado}. COINCIDE.",
                docTotal.Value.ToString("F2", CultureInfo.InvariantCulture),
                calculatedAmount.ToString("F2", CultureInfo.InvariantCulture));
            return false;
        }

        _logger.LogError(
            "DocTotal: SAP {Real} != calculated_amount {Esperado}. DIFERENCIA de {Dif}.",
            docTotal.Value.ToString("F2", CultureInfo.InvariantCulture),
            calculatedAmount.ToString("F2", CultureInfo.InvariantCulture),
            (docTotal.Value - calculatedAmount).ToString("F2", CultureInfo.InvariantCulture));

        return true;
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

/// <summary>Desenlace de integrar una tarea de nota de crédito.</summary>
public sealed record CreditNoteOutcome(
    bool Integrada,
    int? DocNum,
    string? Error,
    bool YaExistiaEnSap = false,
    bool CreadaSinPoderLeerNumero = false,
    bool Simulada = false,
    bool EnsayoEnBorrador = false,
    int? DocEntry = null,
    decimal? DocTotal = null,
    bool TotalDiscrepante = false,
    string? Advertencia = null)
{
    public static CreditNoteOutcome Rechazada(string error) => new(false, null, error);

    public static CreditNoteOutcome SimuladaOk() => new(false, null, null, Simulada: true);

    public static CreditNoteOutcome YaExistia(int docNum) =>
        new(true, docNum, null, YaExistiaEnSap: true);

    public static CreditNoteOutcome Creada(
        int docNum, int? docEntry, decimal? docTotal, bool totalDiscrepante) =>
        new(true, docNum, null,
            DocEntry: docEntry, DocTotal: docTotal, TotalDiscrepante: totalDiscrepante);

    /// <summary>
    /// SAP la creó pero no se pudo leer el número. NO es un error reportable: la
    /// nota existe y reintentarla la duplicaría.
    /// </summary>
    public static CreditNoteOutcome CreadaSinNumero(
        int? docEntry, decimal? docTotal, bool totalDiscrepante) =>
        new(false, null, null,
            CreadaSinPoderLeerNumero: true,
            DocEntry: docEntry, DocTotal: docTotal, TotalDiscrepante: totalDiscrepante);

    /// <summary>
    /// El ensayo en borrador terminó. No hay nada asentado en SAP: sirve para
    /// saber que el payload es válido, no para cerrar la tarea.
    /// </summary>
    public static CreditNoteOutcome EnsayoTerminado(
        int? docEntry, int? docNum, decimal? docTotal, bool totalDiscrepante, string? advertencia) =>
        new(false, docNum, null,
            EnsayoEnBorrador: true,
            DocEntry: docEntry,
            DocTotal: docTotal,
            TotalDiscrepante: totalDiscrepante,
            Advertencia: advertencia);
}
