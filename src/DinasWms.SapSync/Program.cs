using System.Reflection;
using DinasWms.SapSync.Configuration;
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

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IServiceLayerSessionFactory, ServiceLayerSessionFactory>();
builder.Services.AddSingleton<ISapSqlConnectionFactory, SapSqlConnectionFactory>();
builder.Services.AddSingleton<IDocEntryResolver, DocEntryResolver>();
builder.Services.AddSingleton<ForceRequestWatcher>();
builder.Services.AddSingleton<ISyncCycle, SyncCycle>();

// Los tipos de documento se registran acá cuando se construyan, uno a la vez
// (roadmap: IncomingPayments → CreditNotes → facturas → voids → retornos).
// Sin ninguno registrado, un ciclo hace Login/Logout y sirve de latido.
//   builder.Services.AddSingleton<IDocumentSyncStep, IncomingPaymentsSyncStep>();

// Modos de ejecución. El default es el scheduler; los otros dos son
// diagnósticos de una sola pasada:
//   dotnet run -- --RunMode=SmokeTest
//   dotnet run -- --RunMode=SqlProbe --Probe:CardCode=C100012 --Probe:DocNum=6152
var runMode = builder.Configuration["RunMode"] ?? "Scheduler";

switch (runMode.ToLowerInvariant())
{
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
    default:
        builder.Services.AddHostedService<SyncSchedulerWorker>();
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
        host.Services.GetRequiredService<IOptions<SchedulerOptions>>().Value.Validate();
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
