using System.Reflection;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.Sql;
using DinasWms.SapSync.Sync;
using DinasWms.SapSync.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Las credenciales (SAP y SQL) viven en user-secrets, nunca en un archivo
// versionado. Se registra explícitamente (y no solo vía el default de
// Development) para que funcione igual corriendo como consola sin
// DOTNET_ENVIRONMENT seteado.
//   dotnet user-secrets set "ServiceLayer:UserName" "..."
//   dotnet user-secrets set "Sql:Password" "..."
builder.Configuration.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);

builder.Services
    .AddOptions<ServiceLayerOptions>()
    .Bind(builder.Configuration.GetSection(ServiceLayerOptions.SectionName));

builder.Services
    .AddOptions<SchedulerOptions>()
    .Bind(builder.Configuration.GetSection(SchedulerOptions.SectionName));

builder.Services
    .AddOptions<SqlOptions>()
    .Bind(builder.Configuration.GetSection(SqlOptions.SectionName));

builder.Services
    .AddOptions<PaymentsOptions>()
    .Bind(builder.Configuration.GetSection(PaymentsOptions.SectionName));

builder.Services
    .AddOptions<MiddlewareOptions>()
    .Bind(builder.Configuration.GetSection(MiddlewareOptions.SectionName));

builder.Services
    .AddOptions<InvoicesOptions>()
    .Bind(builder.Configuration.GetSection(InvoicesOptions.SectionName));

builder.Services
    .AddOptions<ContinuousOptions>()
    .Bind(builder.Configuration.GetSection(ContinuousOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IServiceLayerSessionFactory, ServiceLayerSessionFactory>();
builder.Services.AddSingleton<ISapSqlConnectionFactory, SapSqlConnectionFactory>();
builder.Services.AddSingleton<IDocEntryResolver, DocEntryResolver>();
builder.Services.AddSingleton<IMiddlewareClient, MiddlewareClient>();
builder.Services.AddSingleton<ForceRequestWatcher>();
builder.Services.AddSingleton<ISyncCycle, SyncCycle>();

builder.Services.AddSingleton<OrderInvoiceIntegrator>();

// Tipos de documento que corren AUTOMÁTICOS. Las notas de crédito NO están acá a
// propósito: se disparan a mano con --RunMode=CreditNoteProbe, por decisión de
// negocio. Agregar una aquí es lo que la vuelve automática.
builder.Services.AddSingleton<IDocumentSyncStep, IncomingPaymentsSyncStep>();
builder.Services.AddSingleton<IDocumentSyncStep, OrderInvoicesSyncStep>();

// Modos de ejecución. El default es el scheduler; los otros dos son
// diagnósticos de una sola pasada:
//   dotnet run -- --RunMode=SmokeTest
//   dotnet run -- --RunMode=SqlProbe --Probe:CardCode=C100012 --Probe:DocNum=6152
var runMode = builder.Configuration["RunMode"] ?? "Continuous";
var modoDesconocido = false;

switch (runMode.ToLowerInvariant())
{
    // Modo normal de operación: sondeo rápido, sesión solo si hay trabajo.
    case "continuous":
        builder.Services.AddHostedService<ContinuousSyncWorker>();
        break;

    // Modo anterior, por ventanas horarias. Se conserva mientras el continuo se
    // prueba en operación real: es código probado y volver a él es cambiar una
    // línea de configuración, no revertir un commit.
    case "scheduler":
        builder.Services.AddHostedService<SyncSchedulerWorker>();
        break;
    case "smoketest":
        builder.Services.AddHostedService<SessionSmokeTestWorker>();
        break;
    case "sqlprobe":
        builder.Services.AddHostedService<SqlProbeWorker>();
        break;
    case "sldiscovery":
        builder.Services.AddHostedService<ServiceLayerDiscoveryWorker>();
        break;
    case "paymentprobe":
        builder.Services.AddHostedService<PaymentProbeWorker>();
        break;
    case "paymentcancel":
        builder.Services.AddHostedService<PaymentCancelWorker>();
        break;
    case "draftinvoiceprobe":
        builder.Services.AddHostedService<DraftInvoiceProbeWorker>();
        break;
    case "invoiceprobe":
        builder.Services.AddHostedService<InvoiceProbeWorker>();
        break;
    case "draftcreditnoteprobe":
        builder.Services.AddHostedService<DraftCreditNoteProbeWorker>();
        break;
    case "creditnoteprobe":
        builder.Services.AddHostedService<CreditNoteProbeWorker>();
        break;
    case "middlewareprobe":
        builder.Services.AddHostedService<MiddlewareProbeWorker>();
        break;
    default:
        // Un RunMode desconocido NO cae al scheduler. Antes sí, y eso hizo que un
        // binario viejo (compilado antes de que existiera un modo nuevo) arrancara
        // el scheduler en silencio cuando se le pidió un diagnóstico: el proceso
        // se quedó corriendo ciclos contra SAP sin que nadie lo pidiera. Fallar
        // ruidosamente es lo correcto.
        modoDesconocido = true;
        break;
}

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = false;
    o.TimestampFormat = "HH:mm:ss ";
});

var host = builder.Build();

var logger = host.Services
    .GetRequiredService<ILoggerFactory>()
    .CreateLogger("DinasWms.SapSync");

if (modoDesconocido)
{
    logger.LogCritical(
        "RunMode desconocido: '{Modo}'. Modos válidos: Continuous, Scheduler, SmokeTest, SqlProbe, " +
        "SlDiscovery, PaymentProbe, PaymentCancel, MiddlewareProbe, DraftInvoiceProbe, " +
        "InvoiceProbe, DraftCreditNoteProbe, CreditNoteProbe. No se arranca nada — en " +
        "particular, NO se " +
        "cae al scheduler, para que un binario desactualizado no termine corriendo ciclos " +
        "contra SAP cuando se le pidió otra cosa.",
        runMode);
    return 1;
}

// Validación temprana: es preferible no arrancar que descubrir a mitad de un
// ciclo que faltaba una credencial o que el horario está mal escrito. Además
// devuelve un código de salida distinto de cero, que es lo que el SCM (cuando
// esto sea un Windows Service) necesita para saber que no arrancó bien.
//
// SqlOptions solo se valida en el modo que lo usa: todavía no hay ningún paso de
// documentos que necesite SQL. Cuando se registre el primero (IncomingPayments),
// pasa a validarse siempre.
try
{
    var esSqlProbe = string.Equals(runMode, "SqlProbe", StringComparison.OrdinalIgnoreCase);

    if (esSqlProbe)
    {
        host.Services.GetRequiredService<IOptions<SqlOptions>>().Value.Validate();
    }
    else
    {
        host.Services.GetRequiredService<IOptions<ServiceLayerOptions>>().Value.Validate();

        // Los pasos registrados necesitan SQL, middleware y el almacén de salida.
        host.Services.GetRequiredService<IOptions<SqlOptions>>().Value.Validate();
        host.Services.GetRequiredService<IOptions<MiddlewareOptions>>().Value.Validate();
        host.Services.GetRequiredService<IOptions<InvoicesOptions>>().Value.Validate();

        // Cada modo valida solo su propia cadencia: exigir la ventana horaria en
        // modo continuo obligaría a mantener configuración que ya no gobierna nada.
        if (string.Equals(runMode, "Scheduler", StringComparison.OrdinalIgnoreCase))
        {
            host.Services.GetRequiredService<IOptions<SchedulerOptions>>().Value.Validate();
        }
        else
        {
            host.Services.GetRequiredService<IOptions<ContinuousOptions>>().Value.Validate();
        }
    }
}
catch (Exception ex)
{
    logger.LogCritical("Configuración inválida, no se arranca: {Message}", ex.Message);
    return 1;
}

logger.LogInformation("Modo de ejecución: {Modo}", runMode);

await host.RunAsync();

return Environment.ExitCode;
