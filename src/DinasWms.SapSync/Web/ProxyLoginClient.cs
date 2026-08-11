using System.Net;
using System.Text;
using System.Text.Json;
using DinasWms.SapSync.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Web;

/// <summary>Resultado de intentar entrar a la interfaz.</summary>
public sealed record ResultadoLogin(bool Ok, string? Rol, string? Nombre, string? Error);

/// <summary>
/// Valida credenciales contra el middleware, sin compartir su clave de firma.
/// </summary>
/// <remarks>
/// Se eligió este camino sobre validar el JWT localmente, por tres razones que
/// se verificaron mirando un token real:
///
/// <list type="number">
/// <item>El token está firmado con <b>HS256</b>, o sea simétrico: validarlo acá
/// exigiría que la clave del middleware exista también en esta máquina, y eso es
/// duplicar un secreto que hoy vive en un solo lugar.</item>
/// <item>Su <c>aud</c> es <c>dinas-wms-vendedores-app-dev</c> — la app de
/// vendedores. Aceptarlo acá sería aceptar una credencial emitida para otro
/// consumidor, y si el middleware rota audiencias o claves esto se rompe sin
/// aviso.</item>
/// <item>Revocar sigue siendo central: se desactiva la cuenta en el WMS y esta
/// pantalla deja de dejar entrar.</item>
/// </list>
///
/// <para>
/// El costo es depender del middleware para iniciar sesión. Es aceptable: sin
/// middleware el sincronizador no tiene cola que procesar igual, y la sesión
/// local dura horas, así que una caída transitoria no expulsa a nadie a mitad de
/// trabajo.
/// </para>
/// <para>
/// ⚠ Este cliente NO comparte handler con el de Service Layer, que acepta
/// cualquier certificado. Acá viajan credenciales de persona.
/// </para>
/// </remarks>
public sealed class ProxyLoginClient
{
    /// <summary>Único rol admitido. La pantalla escribe en SAP.</summary>
    public const string RolRequerido = "ADMIN";

    private readonly HttpClient _http;
    private readonly MiddlewareOptions _options;
    private readonly ILogger<ProxyLoginClient> _logger;

    /// <remarks>
    /// El <see cref="HttpClient"/> viene inyectado y no se construye adentro: sin
    /// eso, el chequeo de rol —lo único que separa a un usuario cualquiera de los
    /// botones que escriben en SAP— no se puede probar sin un middleware vivo.
    /// </remarks>
    public ProxyLoginClient(
        HttpClient http,
        IOptions<MiddlewareOptions> options,
        ILogger<ProxyLoginClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<ResultadoLogin> ValidarAsync(
        string usuario,
        string clave,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(usuario) || string.IsNullOrWhiteSpace(clave))
        {
            return new ResultadoLogin(false, null, null, "Faltan usuario o contraseña.");
        }

        var cuerpo = JsonSerializer.Serialize(new { username = usuario, password = clave });

        HttpResponseMessage respuesta;
        try
        {
            respuesta = await _http
                .PostAsync(
                    _options.LoginPath,
                    new StringContent(cuerpo, Encoding.UTF8, "application/json"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Se distingue de "credenciales malas" a propósito: no poder
            // preguntar y que la respuesta sea "no" son cosas distintas, y
            // confundirlas haría que una caída del middleware parezca una
            // contraseña equivocada.
            _logger.LogError(ex, "No se pudo contactar al middleware para validar el login.");
            return new ResultadoLogin(
                false, null, null,
                "No se pudo contactar al middleware para validar las credenciales.");
        }

        var texto = await respuesta.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (respuesta.StatusCode == HttpStatusCode.Unauthorized)
        {
            _logger.LogWarning("Login rechazado para el usuario {Usuario}.", usuario);
            return new ResultadoLogin(false, null, null, "Usuario o contraseña incorrectos.");
        }

        if (!respuesta.IsSuccessStatusCode)
        {
            _logger.LogError(
                "El middleware respondió {Codigo} al validar el login.", (int)respuesta.StatusCode);
            return new ResultadoLogin(
                false, null, null, $"El middleware respondió {(int)respuesta.StatusCode}.");
        }

        string? rol;
        string? nombre;

        try
        {
            using var doc = JsonDocument.Parse(texto);
            rol = doc.RootElement.TryGetProperty("role", out var r) ? r.GetString() : null;
            nombre = doc.RootElement.TryGetProperty("display_name", out var n) ? n.GetString() : null;
        }
        catch (JsonException)
        {
            return new ResultadoLogin(false, null, null, "El middleware devolvió una respuesta ilegible.");
        }

        if (!string.Equals(rol, RolRequerido, StringComparison.OrdinalIgnoreCase))
        {
            // Credenciales válidas pero rol insuficiente. Se registra: es un
            // intento de entrar a una pantalla que escribe en SAP.
            _logger.LogWarning(
                "El usuario {Usuario} autenticó correctamente pero su rol es '{Rol}', no {Requerido}. " +
                "No se le abre sesión.",
                usuario,
                rol ?? "(sin rol)",
                RolRequerido);

            return new ResultadoLogin(
                false, rol, nombre,
                $"Esta pantalla requiere rol {RolRequerido}; el usuario tiene '{rol ?? "sin rol"}'.");
        }

        _logger.LogInformation(
            "Sesión abierta en la interfaz para {Usuario} ({Nombre}), rol {Rol}.",
            usuario,
            nombre,
            rol);

        return new ResultadoLogin(true, rol, nombre, null);
    }
}
