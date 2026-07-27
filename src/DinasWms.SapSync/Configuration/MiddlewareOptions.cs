namespace DinasWms.SapSync.Configuration;

/// <summary>
/// Conexión al middleware (<c>dinas-wms-middleware</c>), de donde salen las
/// tareas de integración y a donde se reporta el resultado.
/// </summary>
/// <remarks>
/// ⚠ Este cliente NO comparte el <c>HttpClientHandler</c> del cliente de Service
/// Layer. Ese handler acepta cualquier certificado de servidor, y ese bypass está
/// justificado únicamente para una IP conocida de la LAN. Reutilizarlo para
/// hablar con el middleware sería un riesgo real de MITM, y por eso son dos
/// clientes separados por diseño.
/// </remarks>
public sealed class MiddlewareOptions
{
    public const string SectionName = "Middleware";

    /// <summary>URL base del middleware, con slash final.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Usuario para <c>POST auth/login</c>. Va en user-secrets.</summary>
    public string UserName { get; set; } = "";

    /// <summary>Contraseña para <c>POST auth/login</c>. Va en user-secrets.</summary>
    public string Password { get; set; } = "";

    /// <summary>Ruta relativa del login que devuelve el JWT.</summary>
    public string LoginPath { get; set; } = "auth/login";

    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Tope de tareas a procesar por ciclo. Evita que un ciclo se alargue sin
    /// límite si la cola viene muy cargada; lo que sobre se toma en el siguiente.
    /// </summary>
    public int MaxTasksPerCycle { get; set; } = 50;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(BaseUrl))
        {
            throw new InvalidOperationException(
                $"Falta {SectionName}:{nameof(BaseUrl)} (URL base del middleware).");
        }

        if (!BaseUrl.EndsWith('/'))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(BaseUrl)} debe terminar en '/' (actual: '{BaseUrl}'). " +
                "Sin el slash final, Uri descarta el último segmento al resolver rutas relativas.");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(BaseUrl)} no es una URL absoluta válida: '{BaseUrl}'.");
        }

        if (string.IsNullOrWhiteSpace(UserName) || string.IsNullOrWhiteSpace(Password))
        {
            throw new InvalidOperationException(
                $"Faltan credenciales del middleware ({SectionName}:{nameof(UserName)} / " +
                $"{nameof(Password)}). Se cargan con: " +
                $"dotnet user-secrets set \"{SectionName}:{nameof(Password)}\" \"...\"");
        }

        if (string.IsNullOrWhiteSpace(LoginPath))
        {
            throw new InvalidOperationException($"Falta {SectionName}:{nameof(LoginPath)}.");
        }

        if (TimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(TimeoutSeconds)} debe ser mayor que 0.");
        }

        if (MaxTasksPerCycle <= 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxTasksPerCycle)} debe ser mayor que 0.");
        }
    }
}
