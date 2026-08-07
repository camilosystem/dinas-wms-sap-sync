using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DinasWms.SapSync.ServiceLayer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Descubrimiento del bloque de notas de crédito: crea BORRADORES de nota de
/// crédito, mira qué guardó SAP, y los borra.
/// </summary>
/// <remarks>
/// ⚠ Arnés de DESCUBRIMIENTO, no de producción. El payload vive acá adentro a
/// propósito: el tipo definitivo se diseña después, con el contrato ya cerrado.
///
/// Un borrador (<c>ODRF</c>) no asienta contabilidad ni mueve inventario y se
/// puede BORRAR, a diferencia de una nota de crédito, que solo se anula. El
/// borrado va en un <c>finally</c> y se verifica con un GET posterior.
///
/// Salvedad honesta: un borrador NO ejecuta todas las validaciones del documento
/// asentado. Lo que el borrador rechaza, la nota real también lo rechaza; lo que
/// el borrador acepta, casi siempre pasa, pero no es prueba absoluta.
///
/// Escenarios:
///   cuenta       — línea de ÍTEMS con AccountCode 6020 y almacén 07.
///                  ¿SAP respeta la cuenta o la pisa con la del artículo?
///   base-ok      — línea ligada a una línea real de factura, por la cantidad
///                  exacta de esa línea. Confirma que el vínculo funciona.
///   base-exceso  — la MISMA línea base, pero acreditando más de lo facturado.
///                  ¿SAP rechaza, acepta, o acepta con advertencia?
///   base-damaged — ligada Y forzando cuenta 6020 + almacén 07. Es el caso
///                  normal de una devolución dañada que sí tiene factura: los
///                  dos valores difieren de lo que SAP pondría solo.
///
/// Uso:
///   --RunMode=DraftCreditNoteProbe --Probe:Scenario=cuenta --Probe:Confirm=true
/// </remarks>
public sealed class DraftCreditNoteProbeWorker : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<DraftCreditNoteProbeWorker> _logger;

    public DraftCreditNoteProbeWorker(
        IServiceLayerSessionFactory sessionFactory,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IHostApplicationLifetime lifetime,
        ILogger<DraftCreditNoteProbeWorker> logger)
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
            _logger.LogError(ex, "PRUEBA DE NOTA DE CRÉDITO FALLIDA. {Message}", ex.Message);
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task CorrerAsync(CancellationToken cancellationToken)
    {
        var escenario = (_configuration["Probe:Scenario"] ?? "cuenta").ToLowerInvariant();
        var confirmado = string.Equals(
            _configuration["Probe:Confirm"], "true", StringComparison.OrdinalIgnoreCase);

        var cardCode = _configuration["Probe:CardCode"] ?? "C100012";
        var hoy = _timeProvider.GetLocalNow().ToString("yyyy-MM-dd");

        // Factura base para los escenarios ligados: la 7976, creada por este
        // mismo sincronizador. Su línea 2 es GWMDBRA por 1 unidad a 50.25.
        var baseEntry = int.TryParse(_configuration["Probe:BaseEntry"], out var be) ? be : 7976;
        var baseLine = int.TryParse(_configuration["Probe:BaseLine"], out var bl) ? bl : 2;

        // Los escenarios de servicio necesitan otro DocType.
        var docType = escenario.StartsWith("servicio", StringComparison.Ordinal)
            ? "dDocument_Service"
            : "dDocument_Items";

        object linea = escenario switch
        {
            "cuenta" => new
            {
                ItemCode = _configuration["Probe:ItemCode"] ?? "GDC11079",
                Quantity = 1m,
                UnitPrice = 10.00m,
                WarehouseCode = "07",
                AccountCode = "6020",
                TaxCode = "Exempt",
            },

            // Ligada: no se manda ItemCode ni precio; se deja que SAP los copie
            // de la línea base. Es la forma en que el negocio arma hoy sus notas.
            "base-ok" => new
            {
                BaseType = 13,
                BaseEntry = baseEntry,
                BaseLine = baseLine,
                Quantity = 1m,
                WarehouseCode = "01",
            },

            "base-exceso" => new
            {
                BaseType = 13,
                BaseEntry = baseEntry,
                BaseLine = baseLine,
                Quantity = 10m,
                WarehouseCode = "01",
            },

            // El caso NORMAL de Damaged con factura de referencia: ligada, pero
            // forzando cuenta y almacén distintos de los que SAP pondría por
            // determinación (4200) y de los de la factura original (01). Es la
            // combinación que ninguno de los otros escenarios cubre.
            "base-damaged" => new
            {
                BaseType = 13,
                BaseEntry = baseEntry,
                BaseLine = baseLine,
                Quantity = 1m,
                AccountCode = "6020",
                WarehouseCode = "07",
            },

            // ¿Se puede IMPONER el monto en una línea ligada? El contrato trae
            // approved_amount por línea (créditos parciales), pero SAP copia el
            // precio de la línea base. Si no se puede sobreescribir, el crédito
            // parcial ligado no se puede representar.
            "base-precio" => new
            {
                BaseType = 13,
                BaseEntry = baseEntry,
                BaseLine = baseLine,
                Quantity = 1m,
                UnitPrice = 7.77m,
                WarehouseCode = "01",
            },

            // Línea de servicio como las que hace el negocio hoy: sin ItemCode,
            // sin almacén, con cuenta y descripción. Las históricas van con
            // Quantity 0 y el monto en el precio.
            "servicio" => new
            {
                ItemDescription = "PRUEBA descubrimiento SHORT",
                Quantity = 0m,
                UnitPrice = 15.50m,
                AccountCode = "4200",
                TaxCode = "Exempt",
            },

            // La misma línea de servicio pero con cantidad, para ver si el monto
            // se multiplica (y entonces LineTotal deja de ser approved_amount).
            "servicio-cantidad" => new
            {
                ItemDescription = "PRUEBA descubrimiento SHORT con cantidad",
                Quantity = 2m,
                UnitPrice = 15.50m,
                AccountCode = "4200",
                TaxCode = "Exempt",
            },

            _ => throw new InvalidOperationException(
                $"Escenario '{escenario}' desconocido. Válidos: cuenta, base-ok, base-exceso, " +
                "base-damaged, base-precio, servicio, servicio-cantidad."),
        };

        var payload = new
        {
            DocObjectCode = "oCreditNotes",
            DocType = docType,
            CardCode = cardCode,
            DocDate = hoy,
            Comments = $"BORRADOR de descubrimiento ({escenario}), se borra solo",
            DocumentLines = new[] { linea },
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);

        _logger.LogInformation(
            "=== BORRADOR de NOTA DE CRÉDITO (POST /Drafts, DocObjectCode=oCreditNotes) ===\n" +
            "  Escenario: {Escenario} | Cliente: {Cliente}\n{Json}",
            escenario,
            cardCode,
            json);

        if (!confirmado)
        {
            _logger.LogWarning("SIMULACIÓN — no se envió nada. Hace falta --Probe:Confirm=true.");
            return;
        }

        await using var session = await _sessionFactory.OpenAsync(cancellationToken).ConfigureAwait(false);

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
            // Un rechazo NO es un fallo del arnés: para 'base-exceso' es
            // justamente el resultado que se está buscando.
            _logger.LogWarning(
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
                "SAP aceptó el borrador pero no se pudo leer su DocEntry: HAY QUE BORRARLO A MANO.\n{Body}",
                body);
            return;
        }

        _logger.LogInformation("Borrador creado: DocEntry {DocEntry}.", docEntry);

        try
        {
            Reportar("RESPUESTA DEL POST", body);

            var (statusGet, bodyGet) = await session
                .SendForStringAsync(
                    () => new HttpRequestMessage(HttpMethod.Get, $"Drafts({docEntry})"),
                    cancellationToken)
                .ConfigureAwait(false);

            if (statusGet == HttpStatusCode.OK)
            {
                Reportar($"RELECTURA GET Drafts({docEntry})", bodyGet);
            }
            else
            {
                Environment.ExitCode = 1;
                _logger.LogError("No se pudo releer el borrador ({Codigo}): {Body}", (int)statusGet, bodyGet);
            }
        }
        finally
        {
            await BorrarAsync(session, docEntry.Value, cancellationToken).ConfigureAwait(false);
        }
    }

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
            _logger.LogInformation(
                "Borrador {DocEntry} borrado y verificado: el GET posterior da 404.", docEntry);
        }
        else
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "El DELETE respondió bien pero el borrador {DocEntry} TODAVÍA responde ({Codigo}).",
                docEntry,
                (int)statusVerif);
        }
    }

    private void Reportar(string titulo, string body)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            _logger.LogError("=== {Titulo}: cuerpo no interpretable ===\n{Body}", titulo, body);
            return;
        }

        using (doc)
        {
            var raiz = doc.RootElement;
            var texto = new StringBuilder();

            texto.AppendLine(
                $"  DocType = {Texto(raiz, "DocType")} | DocTotal = {Numero(raiz, "DocTotal")} " +
                $"| Series = {Numero(raiz, "Series")} | UserSign = {Numero(raiz, "UserSign")} " +
                $"| WareHouseUpdateType = {Texto(raiz, "WareHouseUpdateType")}");

            if (raiz.TryGetProperty("DocumentLines", out var lineas) &&
                lineas.ValueKind == JsonValueKind.Array)
            {
                foreach (var l in lineas.EnumerateArray())
                {
                    texto.AppendLine(
                        $"  línea {Numero(l, "LineNum")}: Item={Texto(l, "ItemCode")} " +
                        $"Qty={Numero(l, "Quantity")} UnitPrice={Numero(l, "UnitPrice")} " +
                        $"Price={Numero(l, "Price")} LineTotal={Numero(l, "LineTotal")} " +
                        $"AccountCode={Texto(l, "AccountCode")} COGS={Texto(l, "COGSAccountCode")} " +
                        $"Whs={Texto(l, "WarehouseCode")} TaxCode={Texto(l, "TaxCode")} " +
                        $"BaseType={Numero(l, "BaseType")} BaseEntry={Numero(l, "BaseEntry")} " +
                        $"BaseLine={Numero(l, "BaseLine")}");
                }
            }

            _logger.LogInformation("=== {Titulo} ===\n{Texto}", titulo, texto.ToString());
        }
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

    private static string Numero(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetRawText() : "(sin valor)";

    private static string Texto(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "(sin valor)";
}
