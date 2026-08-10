namespace DinasWms.SapSync.Configuration;

/// <summary>
/// Configuración del servidor web que sirve la interfaz de monitoreo.
/// </summary>
/// <remarks>
/// La interfaz vive en el MISMO proceso que el worker, no en una app aparte:
/// así la pantalla lee el estado en vivo de memoria compartida (contadores,
/// ciclo en curso, sesión de SAP abierta) sin plomería entre procesos.
///
/// <para>
/// <b>Nunca se bindea a 0.0.0.0.</b> Las direcciones son explícitas: loopback
/// para poder probar desde la propia máquina, y la IP de Tailscale para que
/// Camilo entre desde su laptop. Con eso la pantalla queda invisible desde la
/// LAN de la oficina, que es una defensa que no cuesta nada y que un firewall
/// mal configurado no puede deshacer por accidente.
/// </para>
/// <para>
/// Va por HTTP y no HTTPS a propósito: Tailscale ya cifra el transporte, y un
/// certificado autofirmado acá agregaría fricción sin ganar nada.
/// </para>
/// </remarks>
public sealed class WebOptions
{
    public const string SectionName = "Web";

    /// <summary>
    /// Si el servidor web arranca. En los modos de diagnóstico se ignora: esos
    /// nunca levantan servidor, tengan lo que tengan acá.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Puerto. 5280 elegido tras mirar qué había en escucha en esta máquina:
    /// libre, fuera de los defaults de ASP.NET Core (5000/5001, que colisionan
    /// con cualquier otra cosa que alguien levante), y distinto del 5257 del
    /// middleware para que una URL diga por sí sola de qué servicio es.
    /// </summary>
    public int Port { get; set; } = 5280;

    /// <summary>
    /// Direcciones donde escuchar. Explícitas y nunca comodín.
    /// </summary>
    /// <remarks>
    /// Arranca VACÍO a propósito. El binder de configuración de .NET no
    /// reemplaza los arrays: los CONCATENA a lo que el objeto ya traía. Con un
    /// valor por defecto acá, un <c>appsettings.json</c> que incluyera loopback
    /// terminaría con la dirección repetida, y Kestrel falla al arrancar
    /// intentando bindear dos veces el mismo puerto ("address already in use").
    /// Si queda vacío, <see cref="Validate"/> cae a loopback, que es el default
    /// seguro: no expone nada.
    /// </remarks>
    public string[] BindAddresses { get; set; } = [];

    public void Validate()
    {
        if (BindAddresses.Length == 0)
        {
            BindAddresses = ["127.0.0.1"];
        }

        // Repetidas no son un error de quien configura, pero sí tumban el
        // arranque, así que se limpian en vez de fallar.
        BindAddresses = BindAddresses.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        if (Port is < 1 or > 65535)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(Port)} debe estar entre 1 y 65535 (actual: {Port}).");
        }

        foreach (var direccion in BindAddresses)
        {
            if (direccion is "0.0.0.0" or "*" or "::")
            {
                throw new InvalidOperationException(
                    $"{SectionName}:{nameof(BindAddresses)} no admite comodines ('{direccion}'). " +
                    "La interfaz expone botones que escriben en SAP; se bindea a direcciones " +
                    "explícitas para que no quede accesible desde la LAN de la oficina.");
            }

            if (!System.Net.IPAddress.TryParse(direccion, out _))
            {
                throw new InvalidOperationException(
                    $"{SectionName}:{nameof(BindAddresses)} tiene un valor que no es una IP " +
                    $"válida: '{direccion}'.");
            }
        }
    }

    /// <summary>URLs completas para Kestrel.</summary>
    public string[] BuildUrls() =>
        BindAddresses.Select(d => $"http://{d}:{Port}").ToArray();
}
