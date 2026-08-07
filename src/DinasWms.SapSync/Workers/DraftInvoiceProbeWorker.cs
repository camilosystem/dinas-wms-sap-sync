using System.Net;
using System.Text;
using System.Text.Json;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.ServiceLayer.Invoices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Crea un BORRADOR de factura en SAP, lo lee, y lo borra. Sirve para contestar
/// la pregunta crítica del bloque de facturas: <b>¿Service Layer respeta el
/// precio que se le manda, o lo recalcula contra su lista de precios?</b>
/// </summary>
/// <remarks>
/// Un borrador (<c>ODRF</c>) no asienta contabilidad ni mueve inventario, y se
/// puede BORRAR — a diferencia de una factura, que solo se puede anular. Por eso
/// la primera prueba real del precio se hace acá y no contra <c>/Invoices</c>.
///
/// El borrador se elimina siempre, incluso si falla la lectura: la limpieza va
/// en un <c>finally</c>. Un borrador olvidado en la base es basura que después
/// alguien tiene que explicar.
///
/// Uso:
///   --RunMode=DraftInvoiceProbe --Probe:CardCode=C101032 --Probe:ItemCode=GDC11079
///   [--Probe:OmitTaxCode=true] [--Probe:KeepDraft=true] --Probe:Confirm=true
/// </remarks>
public sealed class DraftInvoiceProbeWorker : BackgroundService
{
    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DraftInvoiceProbeWorker> _logger;

    public DraftInvoiceProbeWorker(
        IServiceLayerSessionFactory sessionFactory,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IHostApplicationLifetime lifetime,
        ILogger<DraftInvoiceProbeWorker> logger)
    {
        _sessionFactory = sessionFactory;
        _configuration = configuration;
        _timeProvider = timeProvider;
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
            _logger.LogError(ex, "PRUEBA DEL BORRADOR FALLIDA. {Message}", ex.Message);
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task CorrerAsync(CancellationToken cancellationToken)
    {
        var cardCode = _configuration["Probe:CardCode"];
        var itemCode = _configuration["Probe:ItemCode"];
        var almacen = _configuration["Probe:WarehouseCode"] ?? "01";
        var omitirImpuesto = Bandera("Probe:OmitTaxCode");
        var conservar = Bandera("Probe:KeepDraft");
        var confirmado = Bandera("Probe:Confirm");

        if (string.IsNullOrWhiteSpace(cardCode) || string.IsNullOrWhiteSpace(itemCode))
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "Faltan Probe:CardCode y Probe:ItemCode. " +
                "Ej: --Probe:CardCode=C101032 --Probe:ItemCode=GDC11079");
            return;
        }

        var taxCode = omitirImpuesto ? null : "Exempt";

        var escenario = (_configuration["Probe:Scenario"] ?? "precio").ToLowerInvariant();

        // Cada escenario contesta una pregunta distinta. Se corren por separado
        // para que un rechazo no se lleve por delante la respuesta del otro.
        List<InvoiceLine> lineas = escenario switch
        {
            // 0) UnitPrice + DiscountPercent con un precio que NO está en ninguna
            //    lista del ítem. Si SAP recalcula, acá se ve.
            // 1) Descuento del 100%: la promoción que el WMS considera válida.
            // 2) Solo Price, sin UnitPrice: confirma cuál de los dos campos manda.
            "precio" =>
            [
                new InvoiceLine
                {
                    ItemCode = itemCode,
                    Quantity = 3,
                    UnitPrice = 12.34m,
                    DiscountPercent = 25m,
                    WarehouseCode = almacen,
                    TaxCode = taxCode,
                },
                new InvoiceLine
                {
                    ItemCode = itemCode,
                    Quantity = 2,
                    UnitPrice = 33.25m,
                    DiscountPercent = 100m,
                    WarehouseCode = almacen,
                    TaxCode = taxCode,
                },
                new InvoiceLine
                {
                    ItemCode = itemCode,
                    Quantity = 1,
                    Price = 7.77m,
                    WarehouseCode = almacen,
                    TaxCode = taxCode,
                },
            ],

            // El precio 0 es el caso peligroso: si Service Layer trata el 0 como
            // "no me mandaron nada" (como hace con Price), va a poner el precio
            // de lista y a facturarle al cliente una promoción que era gratis.
            "cero" =>
            [
                new InvoiceLine
                {
                    ItemCode = itemCode,
                    Quantity = 2,
                    UnitPrice = 0m,
                    DiscountPercent = 0m,
                    WarehouseCode = almacen,
                    TaxCode = taxCode,
                },
                new InvoiceLine
                {
                    ItemCode = itemCode,
                    Quantity = 1,
                    UnitPrice = 0m,
                    WarehouseCode = almacen,
                    TaxCode = taxCode,
                },
            ],

            _ => throw new InvalidOperationException(
                $"Escenario '{escenario}' desconocido. Válidos: precio, cero."),
        };

        var payload = new InvoicePayload
        {
            DocObjectCode = "oInvoices",
            CardCode = cardCode,
            DocDate = _timeProvider.GetLocalNow().ToString("yyyy-MM-dd"),
            Comments = InvoicePayload.BuildComments(
                $"prueba-{escenario}-borrador", "BORRADOR de prueba, se borra solo"),
            DocumentLines = lineas,
        };

        var json = payload.ToJson();

        _logger.LogInformation(
            "=== BORRADOR a enviar (POST /Drafts, DocObjectCode=oInvoices) ===\n" +
            "  Escenario: {Escenario} | Cliente: {CardCode} | Ítem: {ItemCode} | " +
            "Almacén: {Almacen} | TaxCode: {TaxCode}\n" +
            "  Si SAP RESPETA el precio, los UnitPrice vuelven tal cual se mandaron.\n" +
            "  Si RECALCULA, vuelven con el precio de lista del ítem y " +
            "PriceSource=dpsActivePriceList.\n{Json}",
            escenario,
            cardCode,
            itemCode,
            almacen,
            taxCode ?? "(omitido a propósito)",
            json);

        if (!confirmado)
        {
            _logger.LogWarning(
                "SIMULACIÓN — no se envió nada. Este arnés escribe en SAP (un borrador, que sí se " +
                "puede borrar) y hace falta --Probe:Confirm=true.");
            return;
        }

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogWarning("Enviando POST /Drafts a SAP…");

        var (status, body) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "Drafts")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (status is not (HttpStatusCode.Created or HttpStatusCode.OK))
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "=== SAP RECHAZÓ el borrador ({Codigo} {Status}). Respuesta literal ===\n{Body}",
                (int)status,
                status,
                body);
            return;
        }

        var docEntry = LeerEntero(body, "DocEntry");

        if (docEntry is null)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "SAP aceptó el borrador pero no se pudo leer su DocEntry, así que NO se puede " +
                "borrar automáticamente. HAY QUE BORRARLO A MANO. Respuesta literal:\n{Body}",
                body);
            return;
        }

        _logger.LogInformation(
            "Borrador creado: DocEntry {DocEntry} (DocNum {DocNum}).",
            docEntry,
            LeerEntero(body, "DocNum"));

        try
        {
            ReportarLineas("RESPUESTA DEL POST", body);

            // Releer con un GET aparte: lo que SAP devuelve al crear y lo que
            // quedó guardado no tienen por qué ser lo mismo.
            var (statusGet, bodyGet) = await session
                .SendForStringAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, $"Drafts({docEntry})"),
                    cancellationToken)
                .ConfigureAwait(false);

            if (statusGet == HttpStatusCode.OK)
            {
                ReportarLineas($"RELECTURA GET Drafts({docEntry})", bodyGet);
            }
            else
            {
                Environment.ExitCode = 1;
                _logger.LogError(
                    "No se pudo releer el borrador ({Codigo}). Respuesta: {Body}",
                    (int)statusGet,
                    bodyGet);
            }
        }
        finally
        {
            if (conservar)
            {
                _logger.LogWarning(
                    "--Probe:KeepDraft=true — el borrador {DocEntry} QUEDA en SAP. Hay que borrarlo " +
                    "a mano.",
                    docEntry);
            }
            else
            {
                await BorrarAsync(session, docEntry.Value, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Borra el borrador y comprueba que efectivamente ya no está. No basta con
    /// que el DELETE responda bien: lo que importa es que la base quede limpia.
    /// </summary>
    private async Task BorrarAsync(
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
                "FALLÓ el borrado del borrador {DocEntry} ({Codigo}). HAY QUE BORRARLO A MANO. " +
                "Respuesta: {Body}",
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
            _logger.LogInformation(
                "Borrador {DocEntry} borrado y verificado: el GET posterior da 404. La base queda " +
                "como estaba.",
                docEntry);
        }
        else
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "El DELETE respondió bien pero el borrador {DocEntry} TODAVÍA responde ({Codigo}). " +
                "Hay que revisarlo a mano.",
                docEntry,
                (int)statusVerif);
        }
    }

    /// <summary>Muestra lo que SAP guardó en cada línea, que es el resultado de la prueba.</summary>
    private void ReportarLineas(string titulo, string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            _logger.LogError("=== {Titulo}: el cuerpo no es JSON interpretable ===\n{Body}", titulo, body);
            return;
        }

        using (doc)
        {
            var raiz = doc.RootElement;
            var texto = new StringBuilder();

            texto.AppendLine($"  DocTotal = {Numero(raiz, "DocTotal")} | VatSum = {Numero(raiz, "VatSum")} " +
                             $"| DocType = {Texto(raiz, "DocType")} | Series = {Numero(raiz, "Series")} " +
                             $"| DocCurrency = {Texto(raiz, "DocCurrency")}");

            // UserSign es el usuario de SAP al que queda atribuido el documento
            // (OUSR.InternalKey). Es lo que prueba con qué cuenta escribe de
            // verdad el sincronizador, más allá de con cuál dice que hizo login.
            texto.AppendLine($"  UserSign = {Numero(raiz, "UserSign")}  ← usuario creador en SAP");

            if (raiz.TryGetProperty("DocumentLines", out var lineas) &&
                lineas.ValueKind == JsonValueKind.Array)
            {
                foreach (var linea in lineas.EnumerateArray())
                {
                    texto.AppendLine(
                        $"  línea {Numero(linea, "LineNum")}: Item={Texto(linea, "ItemCode")} " +
                        $"Qty={Numero(linea, "Quantity")} " +
                        $"UnitPrice={Numero(linea, "UnitPrice")} " +
                        $"DiscountPercent={Numero(linea, "DiscountPercent")} " +
                        $"Price={Numero(linea, "Price")} " +
                        $"LineTotal={Numero(linea, "LineTotal")} " +
                        $"PriceSource={Texto(linea, "PriceSource")} " +
                        $"TaxCode={Texto(linea, "TaxCode")} " +
                        $"Whs={Texto(linea, "WarehouseCode")} " +
                        $"MeasureUnit={Texto(linea, "MeasureUnit")}");
                }
            }

            _logger.LogInformation("=== {Titulo} ===\n{Texto}", titulo, texto.ToString());
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

    private static string Numero(JsonElement e, string propiedad) =>
        e.TryGetProperty(propiedad, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetRawText()
            : "(sin valor)";

    private static string Texto(JsonElement e, string propiedad) =>
        e.TryGetProperty(propiedad, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : "(sin valor)";
}
