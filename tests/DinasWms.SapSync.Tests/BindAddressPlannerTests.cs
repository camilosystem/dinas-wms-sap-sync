using DinasWms.SapSync.Configuration;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// La regla que se fija acá es de jerarquía, no de red: <b>facturar es la
/// función del negocio, monitorear es la comodidad</b>. Ninguna condición de la
/// interfaz de monitoreo puede impedir que el worker arranque.
/// </summary>
/// <remarks>
/// Sale de una falla real: el 2026-08-11, tras reiniciar la máquina, el
/// servicio murió seis veces con SocketException 10049 porque Kestrel intentaba
/// bindear la IP de Tailscale antes de que existiera.
/// </remarks>
public class BindAddressPlannerTests
{
    private const string Tailscale = "100.126.181.94";
    private const string Loopback = "127.0.0.1";

    /// <summary>
    /// Una lectura de red que no devuelve nada. Con nombre porque escrito
    /// inline (<c>Guionado([])</c>) el compilador lo toma como "cero lecturas"
    /// en vez de "una lectura vacía", que es otra cosa.
    /// </summary>
    private static readonly string[] SinDirecciones = [];

    /// <summary>
    /// Planner con lecturas de red guionadas: cada llamada devuelve el siguiente
    /// juego de direcciones, y la última se repite. Así se simula "Tailscale
    /// levanta en la tercera vuelta" sin esperar de verdad.
    /// </summary>
    private static (BindAddressPlanner Planner, List<TimeSpan> Esperas) Guionado(
        params string[][] lecturas)
    {
        var esperas = new List<TimeSpan>();
        var vuelta = 0;

        var planner = new BindAddressPlanner(
            () =>
            {
                var lectura = lecturas[Math.Min(vuelta, lecturas.Length - 1)];
                vuelta++;
                return new HashSet<string>(lectura, StringComparer.OrdinalIgnoreCase);
            },
            (cuanto, _) =>
            {
                esperas.Add(cuanto);
                return Task.CompletedTask;
            });

        return (planner, esperas);
    }

    private static WebOptions Opciones(int esperaSegundos = 30, params string[] direcciones)
    {
        var o = new WebOptions
        {
            Enabled = true,
            Port = 5280,
            BindAddresses = direcciones,
            WaitForAddressSeconds = esperaSegundos,
        };
        o.Validate();
        return o;
    }

    [Fact]
    public async Task ConTodasPresentes_noEsperaNada()
    {
        // El arranque normal no puede pagar ningún costo por esta protección.
        var (planner, esperas) = Guionado([Loopback, Tailscale]);

        var plan = await planner.ResolverAsync(Opciones(30, Loopback, Tailscale));

        Assert.Equal([Loopback, Tailscale], plan.Direcciones);
        Assert.Empty(plan.Ausentes);
        Assert.False(plan.CayoALoopback);
        Assert.Empty(esperas);
        Assert.Equal(TimeSpan.Zero, plan.Esperado);
    }

    [Fact]
    public async Task SiLaDireccionApareceTarde_laEsperaLaAlcanza()
    {
        // La carrera de arranque real: Tailscale todavía no asignó la IP en las
        // dos primeras miradas y sí en la tercera.
        var (planner, esperas) = Guionado(
            [Loopback],
            [Loopback],
            [Loopback, Tailscale]);

        var plan = await planner.ResolverAsync(Opciones(30, Loopback, Tailscale));

        Assert.Equal([Loopback, Tailscale], plan.Direcciones);
        Assert.Empty(plan.Ausentes);
        Assert.Equal(2, esperas.Count);
        Assert.Equal(TimeSpan.FromSeconds(2), plan.Esperado);
    }

    [Fact]
    public async Task SiNuncaAparece_arrancaConLoQueHaya_yLoDice()
    {
        // Lo que antes mataba el proceso. Ahora degrada: se pierde el acceso
        // remoto, no la facturación.
        var (planner, esperas) = Guionado([Loopback]);

        var plan = await planner.ResolverAsync(Opciones(5, Loopback, Tailscale));

        Assert.Equal([Loopback], plan.Direcciones);
        Assert.Equal([Tailscale], plan.Ausentes);
        Assert.False(plan.CayoALoopback);
        Assert.Equal(5, esperas.Count);
    }

    [Fact]
    public async Task SinNingunaDisponible_caeALoopback_yNuncaDevuelveVacio()
    {
        // Devolver una lista vacía haría que Kestrel escuche en el default de
        // ASP.NET Core (5000), que es exactamente lo que la configuración
        // explícita existe para evitar.
        var (planner, _) = Guionado(SinDirecciones);

        var plan = await planner.ResolverAsync(Opciones(2, Tailscale));

        Assert.Equal([Loopback], plan.Direcciones);
        Assert.Equal([Tailscale], plan.Ausentes);
        Assert.True(plan.CayoALoopback);
        Assert.NotEmpty(plan.Direcciones);
    }

    [Fact]
    public async Task LoopbackNoSeEspera_aunqueNoEsteEnLaLecturaDeRed()
    {
        // Loopback existe antes que cualquier interfaz. Tratarlo como ausente
        // haría esperar treinta segundos en cada arranque para nada.
        var (planner, esperas) = Guionado(SinDirecciones);

        var plan = await planner.ResolverAsync(Opciones(30, Loopback));

        Assert.Equal([Loopback], plan.Direcciones);
        Assert.Empty(plan.Ausentes);
        Assert.Empty(esperas);
    }

    [Fact]
    public async Task ConEsperaEnCero_miraUnaVezYArranca()
    {
        var (planner, esperas) = Guionado([Loopback]);

        var plan = await planner.ResolverAsync(Opciones(0, Loopback, Tailscale));

        Assert.Equal([Loopback], plan.Direcciones);
        Assert.Equal([Tailscale], plan.Ausentes);
        Assert.Empty(esperas);
    }

    [Fact]
    public async Task LaEsperaSeCortaEnElLimite_noSeCuelgaParaSiempre()
    {
        // Un arranque que no termina es tan malo como uno que falla: el SCM lo
        // da por colgado y la facturación tampoco corre.
        var (planner, esperas) = Guionado([Loopback]);

        var plan = await planner.ResolverAsync(Opciones(10, Loopback, Tailscale));

        Assert.Equal(10, esperas.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), plan.Esperado);
    }
}
