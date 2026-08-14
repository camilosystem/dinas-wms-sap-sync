using System.Net;
using System.Net.NetworkInformation;

namespace DinasWms.SapSync.Configuration;

/// <summary>
/// Dónde va a escuchar la interfaz de monitoreo, después de mirar qué
/// direcciones existen de verdad en la máquina.
/// </summary>
/// <param name="Direcciones">Las que se le pasan a Kestrel.</param>
/// <param name="Ausentes">Configuradas que no aparecieron. Vacío es lo normal.</param>
/// <param name="CayoALoopback">
/// No apareció ninguna de las configuradas y se cayó al default seguro.
/// </param>
/// <param name="Esperado">Cuánto se esperó a que aparecieran.</param>
public sealed record BindAddressPlan(
    string[] Direcciones,
    string[] Ausentes,
    bool CayoALoopback,
    TimeSpan Esperado);

/// <summary>
/// Decide en qué direcciones se puede levantar la interfaz de monitoreo,
/// esperando a las que todavía no existen.
/// </summary>
/// <remarks>
/// Existe por una falla medida, no por precaución teórica. El 2026-08-11, tras
/// un reinicio de la máquina, el servicio murió seis veces seguidas con
/// <c>SocketException (10049) The requested address is not valid in its
/// context</c>: el SCM lo arranca en automático y Kestrel intentaba bindear la
/// IP de Tailscale antes de que esa interfaz la tuviera asignada. Recuperó solo
/// al séptimo intento, dos minutos después.
///
/// <para>
/// El problema de fondo no era el bind, era la jerarquía: la interfaz de
/// monitoreo vive en el mismo proceso que el worker, así que un bind fallido no
/// degradaba la observabilidad — <b>impedía facturar</b>. Una comodidad se
/// había vuelto dependencia dura de la función del negocio.
/// </para>
/// <para>
/// La regla que impone esta clase: <b>facturar es la función, monitorear es la
/// comodidad</b>. Se espera a que la dirección aparezca (la carrera de arranque
/// es el caso común y se resuelve sola en segundos), y si no aparece se arranca
/// con lo que haya —loopback como piso— en vez de no arrancar.
/// </para>
/// <para>
/// Loopback se trata como siempre disponible a propósito: existe antes que
/// cualquier interfaz de red y no depende de que Tailscale, el cable o el Wi-Fi
/// hayan levantado. Es el piso que garantiza que la pantalla sea alcanzable al
/// menos desde la propia máquina.
/// </para>
/// </remarks>
public sealed class BindAddressPlanner
{
    /// <summary>El piso: si no hay ninguna otra, se escucha acá.</summary>
    public const string Loopback = "127.0.0.1";

    private static readonly TimeSpan Intervalo = TimeSpan.FromSeconds(1);

    private readonly Func<IReadOnlySet<string>> _leerLocales;
    private readonly Func<TimeSpan, CancellationToken, Task> _esperar;

    public BindAddressPlanner()
        : this(LeerDireccionesLocales, (cuanto, ct) => Task.Delay(cuanto, ct))
    {
    }

    /// <remarks>
    /// Las dos dependencias se inyectan para poder probar la espera sin que un
    /// test tarde treinta segundos ni dependa de las interfaces de red de la
    /// máquina donde corre.
    /// </remarks>
    public BindAddressPlanner(
        Func<IReadOnlySet<string>> leerLocales,
        Func<TimeSpan, CancellationToken, Task> esperar)
    {
        _leerLocales = leerLocales;
        _esperar = esperar;
    }

    /// <summary>
    /// Espera a que aparezcan las direcciones configuradas y devuelve con cuáles
    /// se puede arrancar. Nunca devuelve una lista vacía.
    /// </summary>
    public async Task<BindAddressPlan> ResolverAsync(
        WebOptions opciones,
        CancellationToken cancellationToken = default)
    {
        var limite = TimeSpan.FromSeconds(opciones.WaitForAddressSeconds);
        var esperado = TimeSpan.Zero;

        string[] ausentes;

        while (true)
        {
            var locales = _leerLocales();

            ausentes = opciones.BindAddresses
                .Where(direccion => !EstaDisponible(direccion, locales))
                .ToArray();

            // Se corta apenas están todas: en el arranque normal no se espera
            // nada, la primera vuelta ya las encuentra.
            if (ausentes.Length == 0 || esperado >= limite)
            {
                break;
            }

            await _esperar(Intervalo, cancellationToken).ConfigureAwait(false);
            esperado += Intervalo;
        }

        if (ausentes.Length == 0)
        {
            return new BindAddressPlan(opciones.BindAddresses, [], CayoALoopback: false, esperado);
        }

        var disponibles = opciones.BindAddresses
            .Except(ausentes, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Sin ninguna disponible se cae a loopback en vez de no levantar la web:
        // la pantalla sigue existiendo para quien entre a la máquina, y sobre
        // todo el proceso arranca, que es lo único que la facturación necesita.
        return disponibles.Length > 0
            ? new BindAddressPlan(disponibles, ausentes, CayoALoopback: false, esperado)
            : new BindAddressPlan([Loopback], ausentes, CayoALoopback: true, esperado);
    }

    private static bool EstaDisponible(string direccion, IReadOnlySet<string> locales) =>
        (IPAddress.TryParse(direccion, out var ip) && IPAddress.IsLoopback(ip))
        || locales.Contains(direccion);

    /// <summary>
    /// Direcciones unicast de las interfaces que están arriba. Una interfaz que
    /// existe pero está caída no sirve: bindearla falla igual.
    /// </summary>
    private static IReadOnlySet<string> LeerDireccionesLocales()
    {
        var direcciones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var interfaz in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (interfaz.OperationalStatus != OperationalStatus.Up)
            {
                continue;
            }

            foreach (var unicast in interfaz.GetIPProperties().UnicastAddresses)
            {
                direcciones.Add(unicast.Address.ToString());
            }
        }

        return direcciones;
    }
}
