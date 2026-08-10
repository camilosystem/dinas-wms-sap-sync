using System.Reflection;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.Observability;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.Sql;
using DinasWms.SapSync.Sync;
using DinasWms.SapSync.Web;
using DinasWms.SapSync.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// ---------------------------------------------------------------------------
// Dos formas de arrancar, un solo registro de servicios.
//
//   · Modos OPERATIVOS (Continuous, Scheduler): pueden levantar el servidor web
//     que sirve la interfaz de monitoreo, en el mismo proceso que el worker.
//   · Modos de DIAGNÓSTICO (los once probes): arrancan headless, sin Kestrel,
//     sin puerto y sin autenticación. Son de una sola pasada y se apagan solos;
//     levantarles un servidor web sería ruido y una superficie de ataque que no
//     necesitan.
//
// El RunMode se lee ANTES de elegir builder, con una configuración mínima
// desechable. El registro de servicios y la validación son compartidos: si las
// dos ramas divergieran, un modo funcionaría y el otro no por razones invisibles.
// ---------------------------------------------------------------------------

var configPrevia = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: true)
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();

var runMode = configPrevia["RunMode"] ?? "Continuous";

var esOperativo =
    string.Equals(runMode, "Continuous", StringComparison.OrdinalIgnoreCase) ||
    string.Equals(runMode, "Scheduler", StringComparison.OrdinalIgnoreCase);

// El servidor web solo se considera en modos operativos. En diagnóstico se
// ignora la configuración a propósito: un probe nunca debe abrir un puerto.
var opcionesWeb = new WebOptions();
configPrevia.GetSection(WebOptions.SectionName).Bind(opcionesWeb);
var conWeb = esOperativo && opcionesWeb.Enabled;

var modoDesconocido = false;

IHost host;

if (conWeb)
{
    opcionesWeb.Validate();

    var builder = WebApplication.CreateBuilder(args);
    Configurar(builder.Configuration, builder.Services, builder.Logging);

    builder.Services.AddSingleton<WebSessions>();
    builder.Services.AddSingleton<ProxyLoginClient>();
    builder.Services.AddSingleton<ManualTriggerService>();

    builder.WebHost.UseUrls(opcionesWeb.BuildUrls());

    var app = builder.Build();

    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapApi();

    host = app;
}
else
{
    var builder = Host.CreateApplicationBuilder(args);
    Configurar(builder.Configuration, builder.Services, builder.Logging);

    host = builder.Build();
}

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

if (conWeb)
{
    logger.LogInformation(
        "Interfaz de monitoreo escuchando en: {Urls}", string.Join(", ", opcionesWeb.BuildUrls()));
}

await host.RunAsync();

return Environment.ExitCode;

// ---------------------------------------------------------------------------

// Registro compartido por las dos ramas. Recibe las tres superficies que ambos
// builders exponen igual, así no hay dos listas de servicios que mantener.
void Configurar(
    IConfigurationManager configuration,
    IServiceCollection services,
    ILoggingBuilder logging)
{
    // Las credenciales (SAP y SQL) viven en user-secrets, nunca en un archivo
    // versionado. Se registra explícitamente (y no solo vía el default de
    // Development) para que funcione igual corriendo como consola sin
    // DOTNET_ENVIRONMENT seteado.
    //   dotnet user-secrets set "ServiceLayer:UserName" "..."
    //   dotnet user-secrets set "Sql:Password" "..."
    configuration.AddUserSecrets(Assembly.GetExecutingAssembly(), optional: true);

    services.AddOptions<ServiceLayerOptions>()
        .Bind(configuration.GetSection(ServiceLayerOptions.SectionName));

    services.AddOptions<SchedulerOptions>()
        .Bind(configuration.GetSection(SchedulerOptions.SectionName));

    services.AddOptions<SqlOptions>()
        .Bind(configuration.GetSection(SqlOptions.SectionName));

    services.AddOptions<PaymentsOptions>()
        .Bind(configuration.GetSection(PaymentsOptions.SectionName));

    services.AddOptions<MiddlewareOptions>()
        .Bind(configuration.GetSection(MiddlewareOptions.SectionName));

    services.AddOptions<InvoicesOptions>()
        .Bind(configuration.GetSection(InvoicesOptions.SectionName));

    services.AddOptions<ContinuousOptions>()
        .Bind(configuration.GetSection(ContinuousOptions.SectionName));

    services.AddOptions<WebOptions>()
        .Bind(configuration.GetSection(WebOptions.SectionName));

    services.AddSingleton(TimeProvider.System);

    // Observabilidad. Se registra en TODOS los modos, no solo con la web: el
    // buffer y el estado en vivo son baratos, y tenerlos siempre significa que
    // el día que haga falta mirar qué pasó no hay que reiniciar con otro modo.
    services.AddSingleton(new LogBuffer(capacity: 2000));
    services.AddSingleton<SyncStatus>();
    services.AddSingleton<LogBufferProvider>();
    services.AddSingleton<ILoggerProvider>(sp => sp.GetRequiredService<LogBufferProvider>());
    services.AddSingleton<IServiceLayerSessionFactory, ServiceLayerSessionFactory>();
    services.AddSingleton<ISapSqlConnectionFactory, SapSqlConnectionFactory>();
    services.AddSingleton<IDocEntryResolver, DocEntryResolver>();
    services.AddSingleton<IMiddlewareClient, MiddlewareClient>();
    services.AddSingleton<ForceRequestWatcher>();

    // Permiso único para correr un ciclo. Singleton a propósito: si hubiera uno
    // por scope, dejaría de ser único y no garantizaría nada.
    services.AddSingleton<SyncCycleGate>();
    services.AddSingleton<ISyncCycle, SyncCycle>();

    services.AddSingleton<OrderInvoiceIntegrator>();

    // Tipos de documento que corren AUTOMÁTICOS. Las notas de crédito NO están
    // acá a propósito: se disparan a mano con --RunMode=CreditNoteProbe, por
    // decisión de negocio. Agregar una aquí es lo que la vuelve automática.
    services.AddSingleton<IDocumentSyncStep, IncomingPaymentsSyncStep>();
    services.AddSingleton<IDocumentSyncStep, OrderInvoicesSyncStep>();

    // Modos de ejecución. El default es el continuo; los demás son diagnósticos
    // de una sola pasada:
    //   dotnet run -- --RunMode=SmokeTest
    //   dotnet run -- --RunMode=SqlProbe --Probe:CardCode=C100012 --Probe:DocNum=6152
    switch (runMode.ToLowerInvariant())
    {
        // Modo normal de operación: sondeo rápido, sesión solo si hay trabajo.
        case "continuous":
            services.AddHostedService<ContinuousSyncWorker>();
            break;

        // Modo anterior, por ventanas horarias. Se conserva mientras el continuo
        // se prueba en operación real: es código probado y volver a él es
        // cambiar una línea de configuración, no revertir un commit.
        case "scheduler":
            services.AddHostedService<SyncSchedulerWorker>();
            break;
        case "smoketest":
            services.AddHostedService<SessionSmokeTestWorker>();
            break;
        case "sqlprobe":
            services.AddHostedService<SqlProbeWorker>();
            break;
        case "sldiscovery":
            services.AddHostedService<ServiceLayerDiscoveryWorker>();
            break;
        case "paymentprobe":
            services.AddHostedService<PaymentProbeWorker>();
            break;
        case "paymentcancel":
            services.AddHostedService<PaymentCancelWorker>();
            break;
        case "draftinvoiceprobe":
            services.AddHostedService<DraftInvoiceProbeWorker>();
            break;
        case "invoiceprobe":
            services.AddHostedService<InvoiceProbeWorker>();
            break;
        case "draftcreditnoteprobe":
            services.AddHostedService<DraftCreditNoteProbeWorker>();
            break;
        case "creditnoteprobe":
            services.AddHostedService<CreditNoteProbeWorker>();
            break;
        case "middlewareprobe":
            services.AddHostedService<MiddlewareProbeWorker>();
            break;
        default:
            // Un RunMode desconocido NO cae al scheduler. Antes sí, y eso hizo
            // que un binario viejo (compilado antes de que existiera un modo
            // nuevo) arrancara el scheduler en silencio cuando se le pidió un
            // diagnóstico: el proceso se quedó corriendo ciclos contra SAP sin
            // que nadie lo pidiera. Fallar ruidosamente es lo correcto.
            modoDesconocido = true;
            break;
    }

    logging.AddSimpleConsole(o =>
    {
        o.SingleLine = false;
        o.TimestampFormat = "HH:mm:ss ";
    });
}
