using System.Text.Json.Serialization;

namespace DinasWms.SapSync.ServiceLayer;

/// <summary>
/// Cuerpo de la respuesta de <c>POST /Login</c>. La forma exacta se confirma
/// contra la respuesta real de SUPPORT_DINAS — los campos aquí son los que
/// Service Layer v1 documenta; si aparece algo más, el smoke test lo registra
/// crudo para poder ajustarlo.
/// </summary>
public sealed class ServiceLayerLoginResponse
{
    [JsonPropertyName("SessionId")]
    public string? SessionId { get; set; }

    [JsonPropertyName("Version")]
    public string? Version { get; set; }

    /// <summary>Minutos de inactividad antes de que SAP cierre la sesión.</summary>
    [JsonPropertyName("SessionTimeout")]
    public int? SessionTimeout { get; set; }
}
