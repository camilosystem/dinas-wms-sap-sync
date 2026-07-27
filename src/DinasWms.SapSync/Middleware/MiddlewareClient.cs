using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DinasWms.SapSync.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Middleware;

/// <summary>
/// Cliente HTTP del middleware. Devuelve cuerpos crudos a propósito: la forma de
/// los contratos se valida contra respuestas reales, no se asume.
/// </summary>
public interface IMiddlewareClient
{
    /// <summary>
    /// Autentica y guarda el JWT. Se llama al inicio de cada ciclo.
    /// </summary>
    Task LoginAsync(CancellationToken cancellationToken);

    Task<(HttpStatusCode StatusCode, string Body)> GetAsync(
        string relativePath,
        CancellationToken cancellationToken);

    Task<(HttpStatusCode StatusCode, string Body)> PostJsonAsync(
        string relativePath,
        string json,
        CancellationToken cancellationToken);

    string BaseUrl { get; }

    /// <summary>Expiración del token según su claim <c>exp</c>, si se pudo leer.</summary>
    DateTimeOffset? TokenExpiresAtUtc { get; }
}

/// <inheritdoc cref="IMiddlewareClient"/>
public sealed class MiddlewareClient : IMiddlewareClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly MiddlewareOptions _options;
    private readonly ILogger<MiddlewareClient> _logger;

    private string? _token;

    public MiddlewareClient(IOptions<MiddlewareOptions> options, ILogger<MiddlewareClient> logger)
    {
        _options = options.Value;
        _logger = logger;
        _options.Validate();

        // Handler POR DEFECTO, con validación de certificado intacta. No se
        // reutiliza el del cliente de Service Layer, que acepta cualquier
        // certificado: ese bypass solo se justifica contra una IP conocida de la
        // LAN, y traerlo acá sería un riesgo real.
        _http = new HttpClient
        {
            BaseAddress = new Uri(_options.BaseUrl),
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds),
        };

        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public string BaseUrl => _options.BaseUrl;

    public DateTimeOffset? TokenExpiresAtUtc { get; private set; }

    public async Task LoginAsync(CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            username = _options.UserName,
            password = _options.Password,
        });

        _logger.LogInformation(
            "Login en el middleware: {Base}{Ruta} (usuario {Usuario})",
            _options.BaseUrl,
            _options.LoginPath,
            _options.UserName);

        // Sin token: el login no lo necesita, y mandar uno viejo podría confundir.
        _token = null;

        var (status, body) = await EnviarCrudoAsync(
            () => new HttpRequestMessage(HttpMethod.Post, _options.LoginPath)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            },
            cancellationToken).ConfigureAwait(false);

        if (status != HttpStatusCode.OK)
        {
            throw new MiddlewareException(
                $"Login rechazado por el middleware ({(int)status} {status}). " +
                $"Usuario '{_options.UserName}'. Respuesta: {body}",
                status,
                body);
        }

        string? token;
        try
        {
            using var doc = JsonDocument.Parse(body);
            token = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
        }
        catch (JsonException ex)
        {
            throw new MiddlewareException(
                "El login respondió 200 pero el cuerpo no es JSON interpretable. Respuesta: " + body,
                status,
                body,
                ex);
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            throw new MiddlewareException(
                "El login respondió 200 pero sin campo 'token'. Respuesta: " + body,
                status,
                body);
        }

        _token = token;
        TokenExpiresAtUtc = LeerExpiracion(token);

        _logger.LogInformation(
            "Login OK en el middleware. Token de {Largo} chars, expira {Expira}.",
            token.Length,
            TokenExpiresAtUtc?.ToString("yyyy-MM-dd HH:mm:ss 'UTC'") ?? "(no se pudo leer 'exp')");
    }

    public Task<(HttpStatusCode StatusCode, string Body)> GetAsync(
        string relativePath,
        CancellationToken cancellationToken) =>
        EnviarConReintentoAsync(
            () => new HttpRequestMessage(HttpMethod.Get, relativePath),
            cancellationToken);

    public Task<(HttpStatusCode StatusCode, string Body)> PostJsonAsync(
        string relativePath,
        string json,
        CancellationToken cancellationToken) =>
        EnviarConReintentoAsync(
            () => new HttpRequestMessage(HttpMethod.Post, relativePath)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            cancellationToken);

    /// <summary>
    /// Envía con el token actual. Ante un 401, re-login y UN reintento.
    /// </summary>
    /// <remarks>
    /// Mismo criterio que con Service Layer: no se trackea la expiración para
    /// renovar de forma proactiva. El token se pide al empezar el ciclo y, si
    /// expira antes de lo esperado, se reintenta una vez. Si vuelve a dar 401 se
    /// aborta — nunca en bucle.
    /// </remarks>
    private async Task<(HttpStatusCode StatusCode, string Body)> EnviarConReintentoAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        var (status, body) = await EnviarCrudoAsync(requestFactory, cancellationToken).ConfigureAwait(false);

        if (status != HttpStatusCode.Unauthorized)
        {
            return (status, body);
        }

        _logger.LogWarning("401 del middleware — el token expiró antes de lo esperado. Re-login y un reintento.");
        await LoginAsync(cancellationToken).ConfigureAwait(false);

        var (statusRetry, bodyRetry) = await EnviarCrudoAsync(requestFactory, cancellationToken)
            .ConfigureAwait(false);

        if (statusRetry == HttpStatusCode.Unauthorized)
        {
            throw new MiddlewareException(
                "401 nuevamente después de un re-login exitoso. Se aborta (no se reintenta en bucle). " +
                "Respuesta: " + bodyRetry,
                statusRetry,
                bodyRetry);
        }

        return (statusRetry, bodyRetry);
    }

    private async Task<(HttpStatusCode StatusCode, string Body)> EnviarCrudoAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        using var request = requestFactory();

        if (!string.IsNullOrWhiteSpace(_token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        }

        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return (response.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            throw new MiddlewareException(
                $"No se pudo contactar el middleware en {_options.BaseUrl}{request.RequestUri}: {ex.Message}",
                innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MiddlewareException(
                $"Timeout ({_options.TimeoutSeconds}s) llamando {request.Method} {request.RequestUri} " +
                "en el middleware.",
                innerException: ex);
        }
    }

    /// <summary>
    /// Lee el claim <c>exp</c> del JWT. Solo para diagnóstico: no se usa para
    /// renovar el token de forma proactiva.
    /// </summary>
    private static DateTimeOffset? LeerExpiracion(string token)
    {
        try
        {
            var partes = token.Split('.');
            if (partes.Length < 2)
            {
                return null;
            }

            var payload = partes[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');

            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));

            return doc.RootElement.TryGetProperty("exp", out var exp) && exp.TryGetInt64(out var segundos)
                ? DateTimeOffset.FromUnixTimeSeconds(segundos)
                : null;
        }
        catch
        {
            // El 'exp' es informativo; si no se puede leer, no vale fallar por eso.
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Error hablando con el middleware.</summary>
public sealed class MiddlewareException : Exception
{
    public MiddlewareException(
        string message,
        HttpStatusCode? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ResponseBody { get; }
}
