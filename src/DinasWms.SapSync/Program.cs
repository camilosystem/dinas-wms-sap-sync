using System.Reflection;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.Sync;
using DinasWms.SapSync.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(args);

// Las credenciales de SAP viven en user-secrets, nunca en un archivo versionado.
// Se registra explícitamente (y no solo vía el default de Development) para que
// funcione igual corriendo como consola sin DOTNET_ENVIRONMENT seteado.
//   dotnet user-secrets set "ServiceLayer:UserName" "..."
//   dotnet user-secrets set "ServiceLayer:Password" "..."
builder.Configuration.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);

builder.Services
    .AddOptions<ServiceLayerOptions>()
    .Bind(builder.Configuration.GetSection(ServiceLayerOptions.SectionName));

builder.Services
    .AddOptions<SchedulerOptions>()
    .Bind(builder.Configuration.GetSection(SchedulerOptions.SectionName));

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<IServiceLayerSessionFactory, ServiceLayerSessionFactory>();
builder.Services.AddSingleton<ForceRequestWatcher>();
builder.Services.AddSingleton<ISyncCycle, SyncCycle>();

// Los tipos de documento se registran acá cuando se construyan, uno a la vez
// (roadmap: IncomingPayments → CreditNotes → facturas → voids → retornos).
// Sin ninguno registrado, un ciclo hace Login/Logout y sirve de latido.
//   builder.Services.AddSingleton<IDocumentSyncStep, IncomingPaymentsSyncStep>();

// Dos modos de ejecución. El default es el scheduler; SmokeTest queda como
// diagnóstico de un solo ciclo, que es la prueba que validó la sesión:
//   dotnet run -- --RunMode=SmokeTest
var esSmokeTest = string.Equals(
    builder.Configuration["RunMode"],
    "SmokeTest",
    StringComparison.OrdinalIgnoreCase);

if (esSmokeTest)
{
    builder.Services.AddHostedService<SessionSmokeTestWorker>();
}
else
{
    builder.Services.AddHostedService<SyncSchedulerWorker>();
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
try
{
    host.Services.GetRequiredService<IOptions<ServiceLayerOptions>>().Value.Validate();
    host.Services.GetRequiredService<IOptions<SchedulerOptions>>().Value.Validate();
}
catch (Exception ex)
{
    logger.LogCritical("Configuración inválida, no se arranca: {Message}", ex.Message);
    return 1;
}

logger.LogInformation(
    "Modo de ejecución: {Modo}",
    esSmokeTest ? "SmokeTest (un ciclo y salir)" : "Scheduler (ventanas programadas)");

await host.RunAsync();

return Environment.ExitCode;
