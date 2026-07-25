namespace DinasWms.SapSync.Configuration;

/// <summary>
/// Configuración de conexión a SAP Business One Service Layer.
/// Se enlaza desde la sección "ServiceLayer" de la configuración.
/// UserName/Password NUNCA vienen de un archivo versionado — se proveen por
/// <c>dotnet user-secrets</c> o variables de entorno.
/// </summary>
public sealed class ServiceLayerOptions
{
    public const string SectionName = "ServiceLayer";

    /// <summary>
    /// URL base de Service Layer, con slash final. Desde la red de oficina se
    /// usa la IP de LAN del servidor (no <c>apisap.dinascorp.com</c>, que es
    /// exclusiva del acceso externo de Attain).
    /// </summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Company DB contra la que se autentica (ej. SUPPORT_DINAS).</summary>
    public string CompanyDB { get; set; } = "";

    public string UserName { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>
    /// Service Layer usa certificado autofirmado. Ver la nota de seguridad en
    /// <c>ServiceLayerHttpClientFactory</c> antes de cambiar esto.
    /// </summary>
    public bool TrustSelfSignedCertificate { get; set; }

    /// <summary>Timeout por llamada HTTP a Service Layer.</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>
    /// Valida que la configuración esté completa. Lanza si falta algo — es
    /// preferible fallar al arrancar que a mitad de un ciclo.
    /// </summary>
    public void Validate()
    {
        var faltantes = new List<string>();

        if (string.IsNullOrWhiteSpace(BaseUrl)) faltantes.Add($"{SectionName}:{nameof(BaseUrl)}");
        if (string.IsNullOrWhiteSpace(CompanyDB)) faltantes.Add($"{SectionName}:{nameof(CompanyDB)}");
        if (string.IsNullOrWhiteSpace(UserName)) faltantes.Add($"{SectionName}:{nameof(UserName)}");
        if (string.IsNullOrWhiteSpace(Password)) faltantes.Add($"{SectionName}:{nameof(Password)}");

        if (faltantes.Count > 0)
        {
            throw new InvalidOperationException(
                "Configuración de Service Layer incompleta. Falta: " +
                string.Join(", ", faltantes) +
                ". Las credenciales se cargan con: dotnet user-secrets set \"ServiceLayer:UserName\" \"...\"");
        }

        if (!BaseUrl.EndsWith('/'))
        {
            // Un BaseAddress sin slash final hace que Uri descarte el último
            // segmento de la ruta al resolver rutas relativas.
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(BaseUrl)} debe terminar en '/' (actual: '{BaseUrl}').");
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(BaseUrl)} no es una URL absoluta válida: '{BaseUrl}'.");
        }
    }
}
