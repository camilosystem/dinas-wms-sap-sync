using System.Net;
using System.Text;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.ServiceLayer.Payments;
using DinasWms.SapSync.Sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Workers;

/// <summary>
/// Crea UN <c>IncomingPayment</c> real en SAP, aplicando el saldo completo de una
/// factura abierta. Es el arnés para validar el payload por ensayo y error.
/// </summary>
/// <remarks>
/// ⚠ ESCRIBE EN SAP. Un documento se puede anular, pero no borrar. Por eso exige
/// <c>--Probe:Confirm=true</c> explícito: sin ese flag arma el payload, lo
/// muestra, y no envía nada.
///
/// Uso:
///   --RunMode=PaymentProbe --Probe:CardCode=C100012 --Probe:DocNum=6918
///   [--Probe:Method=EFECTIVO] [--Probe:Reference=payment_uuid] [--Probe:Confirm=true]
/// </remarks>
public sealed class PaymentProbeWorker : BackgroundService
{
    private readonly IServiceLayerSessionFactory _sessionFactory;
    private readonly IDocEntryResolver _resolver;
    private readonly PaymentsOptions _paymentsOptions;
    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<PaymentProbeWorker> _logger;

    public PaymentProbeWorker(
        IServiceLayerSessionFactory sessionFactory,
        IDocEntryResolver resolver,
        IOptions<PaymentsOptions> paymentsOptions,
        IConfiguration configuration,
        TimeProvider timeProvider,
        IHostApplicationLifetime lifetime,
        ILogger<PaymentProbeWorker> logger)
    {
        _sessionFactory = sessionFactory;
        _resolver = resolver;
        _paymentsOptions = paymentsOptions.Value;
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
            _logger.LogError(ex, "PRUEBA DE PAGO FALLIDA. {Message}", ex.Message);
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private async Task CorrerAsync(CancellationToken cancellationToken)
    {
        var cardCode = _configuration["Probe:CardCode"];
        var docNumTexto = _configuration["Probe:DocNum"];
        var metodo = _configuration["Probe:Method"] ?? "EFECTIVO";
        var paymentUuid = _configuration["Probe:PaymentUuid"];
        var confirmado = string.Equals(
            _configuration["Probe:Confirm"], "true", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(cardCode) || !int.TryParse(docNumTexto, out var docNum))
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "Faltan Probe:CardCode y Probe:DocNum. " +
                "Ej: --Probe:CardCode=C100012 --Probe:DocNum=6918");
            return;
        }

        if (string.IsNullOrWhiteSpace(paymentUuid))
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "Falta Probe:PaymentUuid. Todo pago debe llevar el payment_uuid explícito en " +
                "Remarks: es la forma acordada de rastrearlo y auditarlo contra SAP, y sin él un " +
                "pago creado no se puede relacionar con el que lo originó.");
            return;
        }

        // --- 1. Resolver el DocEntry por el camino real -----------------------
        var factura = await _resolver
            .LookupInvoiceAsync(cardCode, docNum, cancellationToken)
            .ConfigureAwait(false);

        if (!factura.CanApplyPayment)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "No se puede aplicar un pago a client_code={CardCode}, doc_num={DocNum}: " +
                "desenlace {Desenlace}. No se envía nada a SAP.",
                cardCode,
                docNum,
                factura.Outcome);
            return;
        }

        // Por defecto se aplica el saldo completo y el pago vale lo mismo (caso
        // exacto, sin sobrante). Probe:Applied y Probe:CashSum permiten separarlos
        // para probar el caso del sobrante.
        var aplicado = decimal.TryParse(
            _configuration["Probe:Applied"],
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var a)
            ? a
            : factura.OpenAmount!.Value;

        var totalPagado = decimal.TryParse(
            _configuration["Probe:CashSum"],
            System.Globalization.NumberStyles.Number,
            System.Globalization.CultureInfo.InvariantCulture,
            out var c)
            ? c
            : aplicado;

        if (aplicado <= 0 || totalPagado < aplicado)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "Montos inconsistentes: aplicado={Aplicado}, total pagado={Total}. El total no " +
                "puede ser menor que lo aplicado.",
                aplicado,
                totalPagado);
            return;
        }

        if (aplicado > factura.OpenAmount!.Value)
        {
            Environment.ExitCode = 1;
            _logger.LogError(
                "No se aplica {Aplicado} a una factura cuyo saldo es {Saldo}: sobrepagar una " +
                "factura específica no es lo que se quiere probar.",
                aplicado,
                factura.OpenAmount);
            return;
        }

        var sobrante = totalPagado - aplicado;
        var hoy = _timeProvider.GetLocalNow().ToString("yyyy-MM-dd");

        // --- 2. Armar el payload ---------------------------------------------
        var cuenta = _paymentsOptions.RequireAccountFor(metodo);

        var payload = new IncomingPaymentPayload
        {
            CardCode = cardCode,
            DocDate = hoy,
            // Solo ASCII en el contexto: el campo tiene largo limitado y no vale
            // arriesgar problemas de codificación por un guion bonito.
            Remarks = IncomingPaymentPayload.BuildRemarks(
                paymentUuid,
                $"{metodo} fact {docNum}"),
            PaymentInvoices =
            [
                new IncomingPaymentInvoiceLine
                {
                    DocEntry = factura.DocEntry!.Value,
                    SumApplied = aplicado,
                },
            ],
        };

        switch (metodo.ToUpperInvariant())
        {
            case "EFECTIVO":
                payload.CashAccount = cuenta;
                payload.CashSum = totalPagado;
                break;
            case "TRANSFERENCIA":
                payload.TransferAccount = cuenta;
                payload.TransferSum = totalPagado;
                payload.TransferDate = hoy;
                payload.TransferReference = _paymentsOptions.TransferReference;
                break;
            default:
                // CHEQUE no es implementable hasta que el contrato traiga número
                // de cheque y banco. No se improvisa un cheque incompleto.
                throw new InvalidOperationException(
                    $"El método '{metodo}' no está implementado todavía en este arnés.");
        }

        var json = payload.ToJson();

        _logger.LogInformation(
            "=== Payload a enviar ===\n" +
            "  Factura: client_code={CardCode}, doc_num={DocNum} → DocEntry={DocEntry}\n" +
            "  Saldo de la factura: {Saldo}\n" +
            "  Total pagado: {Total} | aplicado: {Aplicado} | SOBRANTE: {Sobrante}\n" +
            "  Método: {Metodo}, cuenta {Cuenta}\n{Json}",
            cardCode,
            docNum,
            factura.DocEntry,
            factura.OpenAmount,
            totalPagado,
            aplicado,
            sobrante,
            metodo,
            cuenta,
            json);

        if (!confirmado)
        {
            _logger.LogWarning(
                "SIMULACIÓN — no se envió nada. Este arnés ESCRIBE en SAP y un documento se " +
                "puede anular pero no borrar, así que hace falta --Probe:Confirm=true.");
            return;
        }

        // --- 3. POST real ----------------------------------------------------
        await using var session = await _sessionFactory
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        _logger.LogWarning("Enviando POST IncomingPayments a SAP (escritura real)…");

        var (status, body) = await session
            .SendForStringAsync(
                () => new HttpRequestMessage(HttpMethod.Post, "IncomingPayments")
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                },
                cancellationToken)
            .ConfigureAwait(false);

        // La respuesta literal de SAP es el resultado, se muestre bonita o no.
        _logger.LogInformation(
            "=== RESPUESTA LITERAL DE SAP ({Codigo} {Status}) ===\n{Body}",
            (int)status,
            status,
            body);

        if (status is HttpStatusCode.Created or HttpStatusCode.OK)
        {
            _logger.LogInformation("=== POST ACEPTADO por SAP ({Codigo}). ===", (int)status);
        }
        else
        {
            Environment.ExitCode = 1;
            _logger.LogError("=== POST RECHAZADO por SAP ({Codigo}). ===", (int)status);
        }
    }
}
