using System.Security.Cryptography;

namespace DinasWms.SapSync.Web;

/// <summary>Una sesión abierta en la interfaz de monitoreo.</summary>
public sealed record WebSession(string Usuario, string Rol, DateTimeOffset Expira);

/// <summary>
/// Sesiones locales de la interfaz, en memoria.
/// </summary>
/// <remarks>
/// <b>El token va en un header <c>Authorization</c>, no en una cookie.</b> Con
/// cookie, el navegador la adjuntaría sola a cualquier request que alguien logre
/// provocar desde otra página, y habría que defenderse de CSRF con antiforgery
/// tokens. Con un header que el JS pone explícitamente, ese ataque no aplica:
/// ninguna página ajena puede leer el token ni hacer que el navegador lo mande.
/// Para una pantalla con botones que crean facturas reales, esa diferencia
/// importa.
///
/// <para>
/// En memoria y no en disco a propósito: un reinicio del sincronizador cierra
/// las sesiones, que es el comportamiento correcto para una herramienta de
/// operación. Son pocos usuarios y volver a entrar cuesta dos segundos.
/// </para>
/// </remarks>
public sealed class WebSessions
{
    private readonly Dictionary<string, WebSession> _sesiones = new(StringComparer.Ordinal);
    private readonly object _candado = new();
    private readonly TimeProvider _timeProvider;

    public WebSessions(TimeProvider timeProvider) => _timeProvider = timeProvider;

    /// <summary>Cuánto dura una sesión sin volver a pedir credenciales.</summary>
    public TimeSpan Duracion { get; set; } = TimeSpan.FromHours(8);

    public string Crear(string usuario, string rol)
    {
        // 32 bytes de aleatoriedad criptográfica. No es un JWT: no necesita
        // llevar información ni ser verificable sin estado, porque el estado
        // vive acá al lado.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        lock (_candado)
        {
            LimpiarVencidas();
            _sesiones[token] = new WebSession(usuario, rol, _timeProvider.GetUtcNow() + Duracion);
        }

        return token;
    }

    /// <summary>Devuelve la sesión si el token es válido y no venció.</summary>
    public WebSession? Validar(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        lock (_candado)
        {
            if (!_sesiones.TryGetValue(token, out var sesion))
            {
                return null;
            }

            if (sesion.Expira <= _timeProvider.GetUtcNow())
            {
                _sesiones.Remove(token);
                return null;
            }

            return sesion;
        }
    }

    public void Cerrar(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return;
        }

        lock (_candado)
        {
            _sesiones.Remove(token);
        }
    }

    public int Activas
    {
        get
        {
            lock (_candado)
            {
                LimpiarVencidas();
                return _sesiones.Count;
            }
        }
    }

    private void LimpiarVencidas()
    {
        var ahora = _timeProvider.GetUtcNow();
        var vencidas = _sesiones.Where(p => p.Value.Expira <= ahora).Select(p => p.Key).ToList();

        foreach (var token in vencidas)
        {
            _sesiones.Remove(token);
        }
    }
}
