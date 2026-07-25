using System.Net;
using System.Text;
using System.Text.Json;
using DinasWms.SapSync.Configuration;
using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.ServiceLayer;

/// <summary>
/// Una sesión de Service Layer, viva durante UN ciclo de trabajo.
/// Se obtiene por <see cref="IServiceLayerSessionFactory.OpenAsync"/> y se
/// cierra con <c>await using</c> — el <c>Dispose</c> hace el <c>Logout</c>.
/// </summary>
public sealed class ServiceLayerSession : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClientHandler _handler;
    private readonly CookieContainer _cookies;
    private readonly ServiceLayerOptions _options;
    private readonly ILogger<ServiceLayerSession> _logger;

    private bool _loggedIn;
    private bool _disposed;

    internal ServiceLayerSession(
        HttpClient http,
        HttpClientHandler handler,
        CookieContainer cookies,
        ServiceLayerOptions options,
        ILogger<ServiceLayerSession> logger)
    {
        _http = http;
        _handler = handler;
        _cookies = cookies;
        _options = options;
        _logger = logger;
    }

    /// <summary>SessionId devuelto por SAP en el cuerpo del Login.</summary>
    public string? SessionId { get; private set; }

    /// <summary>Versión de SAP Business One reportada por Service Layer.</summary>
    public string? Version { get; private set; }

    /// <summary>Minutos de inactividad antes de que SAP cierre la sesión.</summary>
    public int? SessionTimeoutMinutes { get; private set; }

    /// <summary>Cookies que SAP entregó en el Login (solo los nombres).</summary>
    public IReadOnlyList<string> CookieNames { get; private set; } = Array.Empty<string>();

    public bool IsLoggedIn => _loggedIn;

    /// <summary>Cuerpo crudo de la respuesta del Login — útil en esta fase de ensayo y error.</summary>
    public string? RawLoginResponse { get; private set; }

    // ---------------------------------------------------------------- Login

    internal async Task LoginAsync(CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            CompanyDB = _options.CompanyDB,
            UserName = _options.UserName,
            Password = _options.Password,
        });

        _logger.LogInformation(
            "Login en Service Layer: {BaseUrl}Login (CompanyDB={CompanyDB}, UserName={UserName})",
            _options.BaseUrl,
            _options.CompanyDB,
            _options.UserName);

        using var request = new HttpRequestMessage(HttpMethod.Post, "Login")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceLayerException(
                $"No se pudo contactar Service Layer en {_options.BaseUrl}Login: {ex.Message}",
                innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServiceLayerException(
                $"Timeout ({_options.TimeoutSeconds}s) llamando {_options.BaseUrl}Login.",
                innerException: ex);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            RawLoginResponse = body;

            if (!response.IsSuccessStatusCode)
            {
                // El cuerpo de error de Service Layer trae el mensaje real de SAP
                // (ej. company DB inválida, usuario bloqueado, licencia). Es la
                // información más valiosa acá, así que va completa en la excepción.
                throw new ServiceLayerException(
                    $"Login rechazado por Service Layer ({(int)response.StatusCode} {response.StatusCode}). " +
                    $"CompanyDB='{_options.CompanyDB}', UserName='{_options.UserName}'. Respuesta: {body}",
                    response.StatusCode,
                    body);
            }

            ServiceLayerLoginResponse? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<ServiceLayerLoginResponse>(body);
            }
            catch (JsonException ex)
            {
                throw new ServiceLayerException(
                    "El Login respondió 200 pero el cuerpo no es JSON interpretable. Respuesta: " + body,
                    response.StatusCode,
                    body,
                    ex);
            }

            SessionId = parsed?.SessionId;
            Version = parsed?.Version;
            SessionTimeoutMinutes = parsed?.SessionTimeout;

            // La cookie es lo que realmente autentica las llamadas siguientes.
            // Un 200 con SessionId pero sin cookie no sirve, así que se valida.
            CookieNames = _cookies.GetCookies(_http.BaseAddress!)
                .Select(c => c.Name)
                .ToArray();

            if (CookieNames.Count == 0)
            {
                throw new ServiceLayerException(
                    "El Login respondió 200 pero no se recibió ninguna cookie de sesión " +
                    "(se esperaba B1SESSION). Respuesta: " + body,
                    response.StatusCode,
                    body);
            }

            _loggedIn = true;

            _logger.LogInformation(
                "Login OK. SessionId={SessionId}, Version={Version}, SessionTimeout={Timeout} min, cookies=[{Cookies}]",
                Mask(SessionId),
                Version,
                SessionTimeoutMinutes,
                string.Join(", ", CookieNames));
        }
    }

    // ----------------------------------------------------------- Llamadas

    /// <summary>
    /// Envía una petición autenticada con la sesión de este ciclo.
    /// </summary>
    /// <param name="requestFactory">
    /// Construye la petición. Es una fábrica, no una instancia, porque un
    /// <see cref="HttpRequestMessage"/> no se puede reenviar: ante un 401 hay
    /// que armar una petición nueva para el reintento.
    /// </param>
    /// <remarks>
    /// Manejo de 401 a mitad de ciclo: un único re-login + un único reintento.
    /// Si el reintento vuelve a dar 401, se aborta con excepción — nunca se
    /// reintenta en bucle.
    /// </remarks>
    public async Task<HttpResponseMessage> SendAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!_loggedIn)
        {
            throw new InvalidOperationException(
                "La sesión de Service Layer no está autenticada. Ábrela con IServiceLayerSessionFactory.OpenAsync.");
        }

        var response = await SendCoreAsync(requestFactory, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            return response;
        }

        var requestLine = $"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}";
        response.Dispose();

        _logger.LogWarning(
            "401 en {Request} — la sesión expiró antes de lo esperado. Re-login y un único reintento.",
            requestLine);

        // Invalidar las cookies viejas antes del re-login para no mezclar
        // B1SESSION vieja y nueva.
        ExpireSessionCookies();
        _loggedIn = false;
        await LoginAsync(cancellationToken).ConfigureAwait(false);

        var retry = await SendCoreAsync(requestFactory, cancellationToken).ConfigureAwait(false);

        if (retry.StatusCode == HttpStatusCode.Unauthorized)
        {
            var body = await retry.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            retry.Dispose();
            throw new ServiceLayerException(
                $"401 nuevamente en {requestLine} después de un re-login exitoso. " +
                "Se aborta el ciclo (no se reintenta en bucle). Respuesta: " + body,
                HttpStatusCode.Unauthorized,
                body);
        }

        return retry;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        var request = requestFactory();
        try
        {
            return await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new ServiceLayerException(
                $"Fallo de transporte llamando {request.Method} {request.RequestUri}: {ex.Message}",
                innerException: ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ServiceLayerException(
                $"Timeout ({_options.TimeoutSeconds}s) llamando {request.Method} {request.RequestUri}.",
                innerException: ex);
        }
        finally
        {
            request.Dispose();
        }
    }

    /// <summary>
    /// Igual que <see cref="SendAsync"/> pero devuelve el status y el cuerpo como
    /// texto, sin lanzar por códigos de error. Pensado para sondear endpoints en
    /// esta fase de ensayo y error, donde el cuerpo del error es el resultado útil.
    /// </summary>
    public async Task<(HttpStatusCode StatusCode, string Body)> SendForStringAsync(
        Func<HttpRequestMessage> requestFactory,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(requestFactory, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return (response.StatusCode, body);
    }

    // --------------------------------------------------------------- Logout

    /// <summary>
    /// <c>POST /Logout</c>. Libera el slot de sesión del lado de SAP — importante
    /// no dejar sesiones colgadas: cuentan contra los límites de licencia/conexión
    /// de Service Layer, y Attain ya tiene las suyas activas.
    /// </summary>
    public async Task LogoutAsync(CancellationToken cancellationToken)
    {
        if (!_loggedIn)
        {
            return;
        }

        // Se marca como cerrada antes de intentar: si el Logout falla no queremos
        // que un segundo Dispose lo reintente.
        _loggedIn = false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "Logout");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation("Logout OK ({StatusCode}).", (int)response.StatusCode);
            }
            else
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "Logout devolvió {StatusCode}. La sesión puede quedar colgada hasta que SAP la expire. Respuesta: {Body}",
                    (int)response.StatusCode,
                    body);
            }
        }
        catch (Exception ex)
        {
            // Un Logout fallido no debe tumbar el ciclo: el trabajo ya se hizo y
            // SAP expirará la sesión por timeout. Pero sí se reporta.
            _logger.LogWarning(ex, "Fallo al hacer Logout de Service Layer.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // CancellationToken.None a propósito: si el ciclo se canceló, igual
        // queremos intentar liberar el slot de sesión en SAP.
        await LogoutAsync(CancellationToken.None).ConfigureAwait(false);

        _http.Dispose();
        _handler.Dispose();
    }

    // --------------------------------------------------------------- Helpers

    private void ExpireSessionCookies()
    {
        foreach (Cookie cookie in _cookies.GetCookies(_http.BaseAddress!))
        {
            cookie.Expired = true;
        }
    }

    /// <summary>
    /// El SessionId es una credencial viva — no va completo a los logs.
    /// </summary>
    private static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "(vacío)";
        }

        return value.Length <= 8
            ? new string('*', value.Length)
            : $"{value[..6]}…(len={value.Length})";
    }
}
