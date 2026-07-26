using System.Net;
using System.Net.Http.Headers;
using System.Text;
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
    Task<(HttpStatusCode StatusCode, string Body)> GetAsync(
        string relativePath,
        CancellationToken cancellationToken);

    Task<(HttpStatusCode StatusCode, string Body)> PostJsonAsync(
        string relativePath,
        string json,
        CancellationToken cancellationToken);

    string BaseUrl { get; }
}

/// <inheritdoc cref="IMiddlewareClient"/>
public sealed class MiddlewareClient : IMiddlewareClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly MiddlewareOptions _options;
    private readonly ILogger<MiddlewareClient> _logger;

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

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _http.DefaultRequestHeaders.Add(_options.ApiKeyHeader, _options.ApiKey);
        }
    }

    public string BaseUrl => _options.BaseUrl;

    public Task<(HttpStatusCode StatusCode, string Body)> GetAsync(
        string relativePath,
        CancellationToken cancellationToken) =>
        EnviarAsync(() => new HttpRequestMessage(HttpMethod.Get, relativePath), cancellationToken);

    public Task<(HttpStatusCode StatusCode, string Body)> PostJsonAsync(
        string relativePath,
        string json,
        CancellationToken cancellationToken) =>
        EnviarAsync(
            () => new HttpRequestMessage(HttpMethod.Post, relativePath)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            },
            cancellationToken);

    private async Task<(HttpStatusCode StatusCode, string Body)> EnviarAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        using var request = requestFactory();

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
