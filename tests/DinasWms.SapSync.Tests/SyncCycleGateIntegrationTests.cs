using DinasWms.SapSync.Observability;
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
        new(fabrica, [], porton, new SyncStatus(TimeProvider.System), NullLogger<SyncCycle>.Instance);

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
    public async Task UnCicloQueRevienta_igualLiberaElPermiso()
    {
        // El modo de falla que convierte la red de seguridad en un paro total: si
        // un ciclo falla a mitad y el permiso no se libera, a partir de ahí TODO
        // recibe 409 — el bucle continuo, los disparos manuales y el centinela —
        // y el sistema se traba entero sin avisar.
        var porton = new SyncCycleGate(NullLogger<SyncCycleGate>.Instance);
        var fabrica = new FabricaEspia();

        var resultado = await Ciclo(porton, fabrica).RunAsync(SyncCycleTrigger.Forced, default);

        Assert.True(fabrica.FueLlamada);
        Assert.False(resultado.Success);
        Assert.False(resultado.RejectedByConcurrency);

        // Lo que importa: el portón quedó libre pese a la explosión.
        Assert.False(porton.EnUso);
        using var siguiente = await porton.TryEnterAsync("el siguiente");
        Assert.NotNull(siguiente);
    }

    [Fact]
    public async Task DiezCiclosSeguidosQueRevientan_noDejanElPortonTomado()
    {
        // Una racha de fallos es justo cuando un permiso filtrado pasaría
        // desapercibido: todo falla igual, y el motivo real quedaría tapado.
        var porton = new SyncCycleGate(NullLogger<SyncCycleGate>.Instance);

        for (var i = 0; i < 10; i++)
        {
            await Ciclo(porton, new FabricaEspia()).RunAsync(SyncCycleTrigger.Scheduled, default);
        }

        Assert.False(porton.EnUso);
        using var siguiente = await porton.TryEnterAsync("el siguiente");
        Assert.NotNull(siguiente);
    }

    [Fact]
    public async Task UnaExcepcionQueEscapaDelUsing_igualLibera()
    {
        // Este es el contrato que va a heredar el endpoint de disparo manual
        // cuando corra el ciclo en segundo plano: pase lo que pase adentro del
        // using, el permiso vuelve.
        var porton = new SyncCycleGate(NullLogger<SyncCycleGate>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var permiso = await porton.TryEnterAsync("tarea de fondo");
            throw new InvalidOperationException("revienta a mitad");
        });

        Assert.False(porton.EnUso);
        using var siguiente = await porton.TryEnterAsync("el siguiente");
        Assert.NotNull(siguiente);
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
