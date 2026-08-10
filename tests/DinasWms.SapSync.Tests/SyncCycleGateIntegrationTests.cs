using DinasWms.SapSync.ServiceLayer;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// Que el portón funcione aislado no alcanza: lo que importa es que
/// <see cref="SyncCycle"/> lo pida ANTES de abrir sesión con SAP. Si lo pidiera
/// después, un disparo rechazado ya habría gastado un login y una licencia.
/// </summary>
public class SyncCycleGateIntegrationTests
{
    /// <summary>
    /// Fábrica que registra si la llamaron y falla si lo hacen. Sirve para
    /// afirmar lo que NO pasó, que es justo lo que hay que probar acá.
    /// </summary>
    private sealed class FabricaEspia : IServiceLayerSessionFactory
    {
        public bool FueLlamada { get; private set; }

        public Task<ServiceLayerSession> OpenAsync(CancellationToken cancellationToken)
        {
            FueLlamada = true;
            throw new InvalidOperationException(
                "No se debería haber intentado abrir sesión con SAP.");
        }
    }

    private static SyncCycle Ciclo(SyncCycleGate porton, IServiceLayerSessionFactory fabrica) =>
        new(fabrica, [], porton, NullLogger<SyncCycle>.Instance);

    [Fact]
    public async Task ConElPortonTomado_elCicloSeRechazaSinAbrirSesion()
    {
        var porton = new SyncCycleGate(NullLogger<SyncCycleGate>.Instance);
        var fabrica = new FabricaEspia();

        // Simula un ciclo en curso: alguien ya tiene el permiso.
        using var enCurso = await porton.TryEnterAsync("bucle continuo");

        var resultado = await Ciclo(porton, fabrica).RunAsync(SyncCycleTrigger.Forced, default);

        Assert.True(resultado.RejectedByConcurrency);
        Assert.False(resultado.Success);
        Assert.False(fabrica.FueLlamada);
    }

    [Fact]
    public async Task UnRechazo_noSeCuentaComoFallo()
    {
        // La distinción importa: si el rechazo contara como fallo, el back-off
        // ensancharía el intervalo por una razón sana y el sistema se degradaría
        // solo, en silencio.
        var porton = new SyncCycleGate(NullLogger<SyncCycleGate>.Instance);
        using var enCurso = await porton.TryEnterAsync("bucle continuo");

        var resultado = await Ciclo(porton, new FabricaEspia())
            .RunAsync(SyncCycleTrigger.Forced, default);

        Assert.True(resultado.RejectedByConcurrency);
        Assert.Equal(0, resultado.TotalProcessed);
        Assert.Equal(0, resultado.TotalFailed);
    }

    [Fact]
    public async Task UnDisparoManualMientrasCorreElBucle_seRechaza()
    {
        // El escenario real: el bucle continuo está a mitad de un ciclo y Camilo
        // aprieta el botón de la pantalla. El manual tiene que rebotar.
        var porton = new SyncCycleGate(NullLogger<SyncCycleGate>.Instance);

        using var delBucle = await porton.TryEnterAsync(SyncCycleTrigger.Scheduled.ToString());

        var manual = await Ciclo(porton, new FabricaEspia())
            .RunAsync(SyncCycleTrigger.Forced, default);

        Assert.True(manual.RejectedByConcurrency);
        Assert.Contains("ya hay un ciclo en curso", manual.ErrorMessage);
    }

    [Fact]
    public async Task TerminadoElCiclo_elSiguienteDisparoYaNoSeRechaza()
    {
        var porton = new SyncCycleGate(NullLogger<SyncCycleGate>.Instance);

        var delBucle = await porton.TryEnterAsync(SyncCycleTrigger.Scheduled.ToString());
        delBucle!.Dispose();

        var fabrica = new FabricaEspia();
        var manual = await Ciclo(porton, fabrica).RunAsync(SyncCycleTrigger.Forced, default);

        // Ahora sí pasa el portón y llega a intentar abrir sesión: la fábrica
        // espía lo confirma, y el ciclo falla por eso y no por concurrencia.
        Assert.True(fabrica.FueLlamada);
        Assert.False(manual.RejectedByConcurrency);
    }
}
