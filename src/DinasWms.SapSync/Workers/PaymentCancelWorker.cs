using System.Net;
using System.Text.Json;
using DinasWms.SapSync.ServiceLayer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Anula pagos en SAP (<c>POST /IncomingPayments(DocEntry)/Cancel</c>).
/// </summary>
/// <remarks>
/// ⚠ ESCRIBE EN SAP y la anulación no se deshace. Dos salvaguardas:
///
///  1. Exige <c>--Probe:Confirm=true</c>; sin el flag solo muestra qué anularía.
///  2. Antes de anular, lee cada documento y verifica que lo haya creado este
///     sincronizador (marca en <c>Remarks</c>). Un DocEntry mal escrito apuntaría
///     al pago real de un cliente, y anular eso sería un daño contable de verdad.
///     Se puede saltar con <c>--Probe:AllowForeign=true</c>, a conciencia.
///
/// Uso:
///   --RunMode=PaymentCancel --Probe:DocEntries=3065,3066 [--Probe:Confirm=true]
/// </remarks>
public sealed class PaymentCancelWorker : BackgroundService
{
    /// <summary>
    /// Marcas que identifican un documento creado por este sincronizador.
    /// </summary>
    private static readonly string[] MarcasPropias = ["payment_uuid=", "dinas-wms-sap-sync"];

    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PaymentCancelWorker> _logger;

    public PaymentCancelWorker(
        IServiceLayerSessionFactory sessionFactory,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<PaymentCancelWorker> logger)
    {
        _sessionFactory = sessionFactory;
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
            _logger.LogError(ex, "ANULACIÓN FALLIDA. {Message}", ex.Message);
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task CorrerAsync(CancellationToken cancellationToken)
    {
        var lista = _configuration["Probe:DocEntries"];
        var confirmado = string.Equals(
            _configuration["Probe:Confirm"], "true", StringComparison.OrdinalIgnoreCase);
        var permitirAjenos = string.Equals(
            _configuration["Probe:AllowForeign"], "true", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(lista))
        {
            Environment.ExitCode = 1;
            _logger.LogError("Falta Probe:DocEntries. Ej: --Probe:DocEntries=3065,3066");
            return;
        }

        var docEntries = new List<int>();
        foreach (var parte in lista.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(parte, out var valor) || valor <= 0)
            {
                Environment.ExitCode = 1;
                _logger.LogError("DocEntry inválido en la lista: '{Valor}'.", parte);
                return;
            }

            docEntries.Add(valor);
        }

        await using var session = await _sessionFactory
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        foreach (var docEntry in docEntries)
        {
            await ProcesarAsync(session, docEntry, confirmado, permitirAjenos, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!confirmado)
        {
            _logger.LogWarning(
                "SIMULACIÓN — no se anuló nada. La anulación no se deshace, así que hace falta " +
                "--Probe:Confirm=true.");
        }
    }

    private async Task ProcesarAsync(
        ServiceLayerSession session,
        int docEntry,
        bool confirmado,
        bool permitirAjenos,
        CancellationToken cancellationToken)
    {
        // --- Leer el documento antes de tocarlo -------------------------------
        var (statusGet, bodyGet) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(
                    HttpMethod.Get,
                    $"IncomingPayments({docEntry})?$select=DocEntry,DocNum,CardCode,CashSum,TransferSum,Remarks,Cancelled,CancelStatus"),
                cancellationToken)
            .ConfigureAwait(false);

        if (statusGet != HttpStatusCode.OK)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "No se pudo leer el pago {DocEntry} ({Status}). No se anula. Respuesta: {Body}",
                docEntry,
                (int)statusGet,
                bodyGet);
            return;
        }

        using var doc = JsonDocument.Parse(bodyGet);
        var raiz = doc.RootElement;

        var remarks = Texto(raiz, "Remarks");
        var cancelado = string.Equals(Texto(raiz, "Cancelled"), "tYES", StringComparison.OrdinalIgnoreCase);

        _logger.LogInformation(
            "Pago {DocEntry}: DocNum={DocNum}, CardCode={CardCode}, CashSum={Cash}, " +
            "TransferSum={Transfer}, Cancelled={Cancelado}\n  Remarks: {Remarks}",
            docEntry,
            Texto(raiz, "DocNum"),
            Texto(raiz, "CardCode"),
            Texto(raiz, "CashSum"),
            Texto(raiz, "TransferSum"),
            Texto(raiz, "Cancelled"),
            remarks ?? "(vacío)");

        if (cancelado)
        {
            _logger.LogInformation("  → ya estaba anulado. Se omite.");
            return;
        }

        var esPropio = remarks is not null &&
            MarcasPropias.Any(m => remarks.Contains(m, StringComparison.OrdinalIgnoreCase));

        if (!esPropio && !permitirAjenos)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "  → NO SE ANULA: el Remarks de este pago no tiene la marca de este " +
                "sincronizador, así que puede ser un pago real de un cliente. Si de verdad " +
                "querías anularlo, usar --Probe:AllowForeign=true.");
            return;
        }

        if (!confirmado)
        {
            _logger.LogWarning("  → SE ANULARÍA (simulación).");
            return;
        }

        // --- Anular -----------------------------------------------------------
        var (status, body) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Post, $"IncomingPayments({docEntry})/Cancel"),
                cancellationToken)
            .ConfigureAwait(false);

        if (status is HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.Created)
        {
            _logger.LogInformation(
                "  → ANULADO. Respuesta de SAP: {Codigo} {Status}{Cuerpo}",
                (int)status,
                status,
                string.IsNullOrWhiteSpace(body) ? " (sin cuerpo)" : "\n" + body);
        }
        else
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "  → SAP RECHAZÓ la anulación ({Codigo} {Status}). Respuesta: {Body}",
                (int)status,
                status,
                body);
        }
    }

    private static string? Texto(JsonElement raiz, string propiedad) =>
        raiz.TryGetProperty(propiedad, out var valor) && valor.ValueKind != JsonValueKind.Null
            ? valor.ToString()
            : null;
}
