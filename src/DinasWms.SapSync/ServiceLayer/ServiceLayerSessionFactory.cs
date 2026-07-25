using System.Net;
using DinasWms.SapSync.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.ServiceLayer;

/// <inheritdoc cref="IServiceLayerSessionFactory"/>
public sealed class ServiceLayerSessionFactory : IServiceLayerSessionFactory
{
    private readonly ServiceLayerOptions _options;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<ServiceLayerSessionFactory> _logger;

    public ServiceLayerSessionFactory(
        IOptions<ServiceLayerOptions> options,
        ILoggerFactory loggerFactory)
    {
        _options = options.Value;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<ServiceLayerSessionFactory>();
    }

    public async Task<ServiceLayerSession> OpenAsync(CancellationToken cancellationToken)
    {
        _options.Validate();

        // Un HttpClientHandler + CookieContainer NUEVOS por ciclo, a propósito:
        //
        //  1. Las cookies de sesión (B1SESSION / ROUTEID) viven en el
        //     CookieContainer y se persisten automáticamente entre las llamadas
        //     del mismo ciclo. Un container nuevo por ciclo garantiza que no se
        //     arrastre la cookie de una sesión ya cerrada (que es justo lo que
        //     pasaría con los handlers reciclados de IHttpClientFactory).
        //  2. El bypass de validación de certificado de abajo queda contenido en
        //     este handler y no puede filtrarse a ningún otro HttpClient del
        //     proceso.
        //
        // El costo de crear un handler por ciclo es irrelevante aquí: los ciclos
        // corren por ventanas (minutos/horas), no en bucle caliente.
        var cookies = new CookieContainer();
        var handler = new HttpClientHandler
        {
            CookieContainer = cookies,
            UseCookies = true,
            // La redirección automática perdería el método/cuerpo del POST y
            // enmascararía problemas de ruta; preferimos ver el 3xx.
            AllowAutoRedirect = false,
        };

        if (_options.TrustSelfSignedCertificate)
        {
            // ⚠ SEGURIDAD: esto acepta CUALQUIER certificado de servidor, no
            // solo el autofirmado de Service Layer. Es aceptable acá porque la
            // conexión es LAN-local contra un servidor conocido y controlado
            // (192.168.11.200), y porque este handler es exclusivo del cliente
            // de Service Layer. NO reutilices este handler ni este HttpClient
            // para llamar a nada más (middleware, internet, etc.) — ahí el
            // bypass sí sería un riesgo real de MITM.
            // Ojo: el miembro se llama ...Validator (no ...Validation, como
            // aparece en instrucciones-sap-sync-arranque.md §3).
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        HttpClient? http = null;
        ServiceLayerSession? session = null;
        try
        {
            http = new HttpClient(handler, disposeHandler: false)
            {
                BaseAddress = new Uri(_options.BaseUrl),
                Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
            };

            session = new ServiceLayerSession(
                http,
                handler,
                cookies,
                _options,
                _loggerFactory.CreateLogger<ServiceLayerSession>());

            await session.LoginAsync(cancellationToken).ConfigureAwait(false);
            return session;
        }
        catch
        {
            // Si el login falla no hay sesión que cerrar, pero sí transporte que
            // liberar. DisposeAsync de la sesión no intenta Logout si nunca hubo
            // login exitoso.
            if (session is not null)
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                http?.Dispose();
                handler.Dispose();
            }

            _logger.LogError(
                "No se pudo abrir la sesión de Service Layer contra {BaseUrl} (CompanyDB {CompanyDB}).",
                _options.BaseUrl,
                _options.CompanyDB);
            throw;
        }
    }
}
