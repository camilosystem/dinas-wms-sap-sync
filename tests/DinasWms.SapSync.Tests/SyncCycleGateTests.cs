using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// El portón es la pieza donde un problema se esconde callado: si dejara pasar a
/// dos, no se vería como excepción sino como documentos duplicados en SAP días
/// después. Por eso se prueba con contención real y no solo verificando que el
/// semáforo exista.
/// </summary>
public class SyncCycleGateTests
{
    private static SyncCycleGate Porton() => new(NullLogger<SyncCycleGate>.Instance);

    [Fact]
    public async Task ElSegundoQueLlega_quedaAfuera()
    {
        var porton = Porton();

        using var primero = await porton.TryEnterAsync("bucle continuo");
        var segundo = await porton.TryEnterAsync("disparo manual");

        Assert.NotNull(primero);
        Assert.Null(segundo);
    }

    [Fact]
    public async Task AlLiberarse_elSiguientePuedeEntrar()
    {
        var porton = Porton();

        var primero = await porton.TryEnterAsync("bucle continuo");
        Assert.NotNull(primero);
        primero!.Dispose();

        using var segundo = await porton.TryEnterAsync("disparo manual");
        Assert.NotNull(segundo);
    }

    [Fact]
    public async Task ConVeinteCompitiendoALaVez_soloUnoEntra()
    {
        // Contención de verdad: veinte tareas arrancando juntas contra el mismo
        // portón. Es el escenario que un test secuencial no cubre.
        var porton = Porton();
        var largada = new TaskCompletionSource();
        var permisos = new List<IDisposable?>();
        var candado = new object();

        var intentos = Enumerable.Range(0, 20).Select(async i =>
        {
            await largada.Task;
            var permiso = await porton.TryEnterAsync($"competidor {i}");
            lock (candado)
            {
                permisos.Add(permiso);
            }
        }).ToArray();

        largada.SetResult();
        await Task.WhenAll(intentos);

        Assert.Equal(1, permisos.Count(p => p is not null));
        Assert.Equal(19, permisos.Count(p => p is null));

        foreach (var p in permisos)
        {
            p?.Dispose();
        }
    }

    [Fact]
    public async Task MientrasHayCicloEnCurso_seSabeQuienLoTiene()
    {
        // La pantalla necesita poder decir "hay un ciclo corriendo, disparado
        // por X, hace N segundos" en vez de un 409 pelado.
        var porton = Porton();

        Assert.False(porton.EnUso);
        Assert.Null(porton.Ocupante);

        using var permiso = await porton.TryEnterAsync("bucle continuo");

        Assert.True(porton.EnUso);
        Assert.Equal("bucle continuo", porton.Ocupante!.Value.Titular);
    }

    [Fact]
    public async Task AlSoltar_vuelveAQuedarLibre()
    {
        var porton = Porton();

        var permiso = await porton.TryEnterAsync("bucle continuo");
        permiso!.Dispose();

        Assert.False(porton.EnUso);
        Assert.Null(porton.Ocupante);
    }

    [Fact]
    public async Task LiberarDosVeces_noDejaEntrarADos()
    {
        // Un doble Dispose soltaría el semáforo de más y permitiría dos ciclos
        // simultáneos: exactamente lo que el portón existe para impedir.
        var porton = Porton();

        var permiso = await porton.TryEnterAsync("bucle continuo");
        permiso!.Dispose();
        permiso.Dispose();

        using var uno = await porton.TryEnterAsync("uno");
        var dos = await porton.TryEnterAsync("dos");

        Assert.NotNull(uno);
        Assert.Null(dos);
    }

    [Fact]
    public async Task ConEsperaConfigurada_aguantaHastaQueSeLibere()
    {
        var porton = Porton();
        var primero = await porton.TryEnterAsync("bucle continuo");

        var esperando = porton.TryEnterAsync("disparo manual", TimeSpan.FromSeconds(5));
        Assert.False(esperando.IsCompleted);

        primero!.Dispose();

        using var segundo = await esperando;
        Assert.NotNull(segundo);
    }
}
