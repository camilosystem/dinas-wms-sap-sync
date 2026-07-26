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

    /// <summary>
    /// Credencial para los endpoints <c>/admin/sap-sync/*</c>. Va en
    /// user-secrets, nunca en un archivo versionado.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Nombre del header por el que viaja la credencial. Se deja configurable
    /// porque el esquema exacto lo define el middleware, no este repo.
    /// </summary>
    public string ApiKeyHeader { get; set; } = "X-Api-Key";

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
