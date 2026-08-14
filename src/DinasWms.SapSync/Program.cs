using System.Net.Sockets;
using System.Reflection;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.Observability;
using DinasWms.SapSync.Persistence;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.Sql;
using DinasWms.SapSync.Sync;
using DinasWms.SapSync.Web;
using DinasWms.SapSync.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.WindowsServices;
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

// Dónde terminó escuchando la web. Se resuelve antes de construir el host y se
// registra después, cuando ya hay logger — mismo patrón que modoDesconocido.
BindAddressPlan? planWeb = null;

IHost host;

// Como servicio, el directorio actual del proceso es C:\Windows\System32. Si no
// se fija el content root, el host busca appsettings.json ahí, no lo encuentra, y
// arranca con la configuración por defecto EN SILENCIO — que es la peor forma de
// fallar: el servicio queda "corriendo" apuntando a cualquier lado.
var comoServicio = WindowsServiceHelpers.IsWindowsService();
var raiz = comoServicio ? AppContext.BaseDirectory : Directory.GetCurrentDirectory();

if (conWeb)
{
    opcionesWeb.Validate();

    // Cuando el SCM arranca el servicio al encender la máquina, la IP de
    // Tailscale puede no existir todavía. Bindear una dirección ausente mata el
    // proceso con SocketException 10049 — y con el proceso se va la
    // facturación, que no tiene nada que ver con el monitor. Se espera a que
    // aparezca y, si no aparece, se arranca con lo que haya.
    planWeb = await new BindAddressPlanner().ResolverAsync(opcionesWeb);

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args,
        ContentRootPath = raiz,
    });
    Configurar(builder.Configuration, builder.Services, builder.Logging);

    builder.Services.AddSingleton<WebSessions>();
    builder.Services.AddSingleton<ManualTriggerService>();

    // El HttpClient del proxy de login se configura acá y se inyecta, para que
    // el chequeo de rol sea probable sin un middleware vivo. NO comparte handler
    // con el de Service Layer, que acepta cualquier certificado: por acá viajan
    // credenciales de persona.
    builder.Services.AddHttpClient<ProxyLoginClient>((sp, http) =>
    {
        var mw = sp.GetRequiredService<IOptions<MiddlewareOptions>>().Value;
        http.BaseAddress = new Uri(mw.BaseUrl);
        http.Timeout = TimeSpan.FromSeconds(mw.TimeoutSeconds);
    });

    builder.WebHost.UseUrls(opcionesWeb.BuildUrls(planWeb.Direcciones));

    var app = builder.Build();

    app.UseDefaultFiles();
    app.UseStaticFiles();
    app.MapApi();

    host = app;
}
else
{
    host = ConstruirHeadless();
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

if (conWeb && planWeb is not null)
{
    if (planWeb.Ausentes.Length > 0)
    {
        logger.LogWarning(
            "Estas direcciones configuradas no aparecieron tras esperar {Esperado:0}s: {Ausentes}. " +
            "La interfaz de monitoreo arranca solo en {Urls}{Aviso}. El worker NO se detiene por " +
            "esto: facturar es la función del negocio, monitorear es la comodidad.",
            planWeb.Esperado.TotalSeconds,
            string.Join(", ", planWeb.Ausentes),
            string.Join(", ", opcionesWeb.BuildUrls(planWeb.Direcciones)),
            planWeb.CayoALoopback ? " (solo loopback: no se llega desde Tailscale)" : string.Empty);
    }
    else
    {
        logger.LogInformation(
            "Interfaz de monitoreo escuchando en: {Urls}",
            string.Join(", ", opcionesWeb.BuildUrls(planWeb.Direcciones)));
    }
}

// StartAsync + WaitForShutdownAsync en vez de RunAsync, que parece lo mismo y
// no lo es: RunAsync DESECHA el host en su finally, así que cuando el bind
// falla el contenedor —y con él el logger y todos los proveedores— ya está
// muerto antes de que se pueda escribir una sola línea explicando por qué. Acá
// el ciclo de vida se maneja a mano para poder avisar y después degradar.
try
{
    await host.StartAsync();
}
catch (Exception ex) when (conWeb && EsFalloAlBindear(ex))
{
    // Última instancia. La espera de BindAddressPlanner cubre la dirección que
    // todavía no existe; esto cubre lo que ninguna espera arregla — el puerto
    // ocupado por un proceso viejo (10048), que es justamente lo que pasa
    // cuando el servicio crashea y el SCM lo reinicia enseguida. Sin esta
    // rama el worker moriría en bucle y la facturación quedaría caída
    // indefinidamente por culpa del monitor, que es la inversión de
    // prioridades que este arreglo existe para prohibir.
    logger.LogCritical(
        ex,
        "Kestrel no pudo bindear ninguna dirección, así que NO hay interfaz de monitoreo. " +
        "El sincronizador arranca igual, sin pantalla: facturar no depende del monitor. " +
        "Revisar si otro proceso tiene tomado el puerto {Puerto}.",
        opcionesWeb.Port);

    // El worker arranca ANTES que Kestrel, así que a esta altura ya está
    // corriendo. Hay que detenerlo de verdad antes de construir el segundo
    // host: dos bucles vivos contra la misma cola sería peor que no tener
    // ninguno. El portón (SyncCycleGate) es por proceso y no protege de esto.
    try
    {
        await host.StopAsync();
    }
    catch (Exception exParada)
    {
        logger.LogWarning(exParada, "Fallo al detener el host a medio arrancar. Se sigue igual.");
    }

    await DesecharAsync(host);

    using var headless = ConstruirHeadless();
    await headless.RunAsync();

    return Environment.ExitCode;
}

await host.WaitForShutdownAsync();
await DesecharAsync(host);

return Environment.ExitCode;

// RunAsync hacía esto solo; al manejar el arranque a mano hay que desecharlo
// a mano también, o el archivo SQLite y el puerto quedan tomados.
static async ValueTask DesecharAsync(IHost elHost)
{
    if (elHost is IAsyncDisposable asincrono)
    {
        await asincrono.DisposeAsync();
    }
    else
    {
        elHost.Dispose();
    }
}

// Kestrel envuelve el fallo de bind en IOException; el detalle real
// (10049 dirección inexistente, 10048 puerto ocupado) viaja adentro.
static bool EsFalloAlBindear(Exception excepcion)
{
    for (var actual = excepcion; actual is not null; actual = actual.InnerException)
    {
        if (actual is SocketException
            || actual.GetType().Name == "AddressInUseException"
            || (actual is IOException && actual.Message.Contains(
                    "Failed to bind", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
    }

    return false;
}

// ---------------------------------------------------------------------------

// Host sin servidor web: el worker y nada más. Lo usan los modos de
// diagnóstico y, sobre todo, la caída elegante cuando la web no puede bindear.
IHost ConstruirHeadless()
{
    var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
    {
        Args = args,
        ContentRootPath = raiz,
    });

    Configurar(builder.Configuration, builder.Services, builder.Logging);

    return builder.Build();
}

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

    // Overrides de la pantalla, respaldados por SQLite. Se agrega ÚLTIMO para
    // que gane sobre appsettings.json y user-secrets — la superposición la hace
    // el propio sistema de configuración, y por eso IOptionsMonitor recibe el
    // aviso de cambio gratis. El archivo JSON nunca se reescribe.
    var almacen = new LocalStore(Path.Combine(AppContext.BaseDirectory, "sap-sync.db"));
    var fuenteSqlite = new SqliteConfigurationSource(almacen);
    configuration.Add(fuenteSqlite);

    services.AddSingleton(almacen);
    services.AddSingleton(fuenteSqlite);

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

    // Corre igual como consola y como Windows Service. Cuando el SCM lo arranca,
    // esto además fija el content root en el directorio del ejecutable — sin
    // eso un servicio busca appsettings.json en C:\Windows\System32 y arranca
    // con la configuración por defecto, en silencio.
    services.AddWindowsService(o => o.ServiceName = "DinasWmsSapSync");

    services.AddSingleton(TimeProvider.System);

    // Observabilidad. Se registra en TODOS los modos, no solo con la web: el
    // buffer y el estado en vivo son baratos, y tenerlos siempre significa que
    // el día que haga falta mirar qué pasó no hay que reiniciar con otro modo.
    services.AddSingleton(new LogBuffer(capacity: 2000));
    services.AddSingleton(sp =>
    {
        var estado = new SyncStatus(sp.GetRequiredService<TimeProvider>())
        {
            Historial = sp.GetRequiredService<LocalStore>(),
        };
        return estado;
    });
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
