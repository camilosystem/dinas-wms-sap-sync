using System.Reflection;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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

builder.Services.AddSingleton<IServiceLayerSessionFactory, ServiceLayerSessionFactory>();

// Fase de arranque: el único worker es la prueba de sesión. El scheduler de
// ventanas y el consumo de /admin/sap-sync/* del middleware son fases siguientes.
builder.Services.AddHostedService<SessionSmokeTestWorker>();

builder.Logging.AddSimpleConsole(o =>
{
    o.SingleLine = false;
    o.TimestampFormat = "HH:mm:ss ";
});

var host = builder.Build();
await host.RunAsync();
