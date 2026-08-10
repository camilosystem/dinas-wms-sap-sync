using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Sync;
using DinasWms.SapSync.Workers;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// El disparo por horario y por forzado ya se verificó contra SAP en real (5
/// ciclos, incluido uno forzado). Lo que queda por cubrir es el apagado: que
/// detener el servicio no abra sesiones contra SAP innecesariamente.
/// </summary>
public class SyncSchedulerWorkerTests
{
    [Fact]
    public async Task Si_ya_esta_cancelado_al_arrancar_no_corre_ningun_ciclo()
    {
        var (worker, ciclo) = Crear(runOnStartup: false);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, ciclo.Ejecuciones);
    }

    [Fact]
    public async Task RunOnStartup_no_dispara_si_el_servicio_ya_se_esta_deteniendo()
    {
        // Sin la guarda de cancelación, un arranque/parada inmediato abriría una
        // sesión de Service Layer para nada — y las sesiones activas cuentan
        // contra los límites de licencia que se comparten con Attain.
        var (worker, ciclo) = Crear(runOnStartup: true);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await worker.StartAsync(cts.Token);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(0, ciclo.Ejecuciones);
    }

    [Fact]
    public async Task Configuracion_invalida_falla_al_arrancar_y_no_corre_ciclos()
    {
        var opciones = new SchedulerOptions
        {
            ActiveFrom = "no-es-una-hora",
            ForceFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt"),
        };

        var ciclo = new CicloStub();
        var worker = new SyncSchedulerWorker(
            ciclo,
            new ForceRequestWatcher(
                Options.Create(opciones), new FakeEnvironment(), NullLogger<ForceRequestWatcher>.Instance),
            Options.Create(opciones),
            TimeProvider.System,
            NullLogger<SyncSchedulerWorker>.Instance);

        // La excepción puede salir por DOS caminos, y cuál de los dos es una
        // carrera con el thread pool. BackgroundService.StartAsync hace:
        //
        //     _executeTask = ExecuteAsync(token);
        //     if (_executeTask.IsCompleted) return _executeTask;   // <- propaga
        //     return Task.CompletedTask;
        //
        // Si la continuación de ExecuteAsync corre antes de ese IsCompleted, la
        // excepción sale por StartAsync; si no, queda en ExecuteTask. Esperar solo
        // uno de los dos hace la prueba inestable (falló ~1 de cada 3 corridas).
        // Cubrir ambos la vuelve determinista sin tapar nada.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await worker.StartAsync(CancellationToken.None);

            if (worker.ExecuteTask is not null)
            {
                await worker.ExecuteTask;
            }
        });

        Assert.Contains("HH:mm", ex.Message);
        Assert.Equal(0, ciclo.Ejecuciones);
    }

    private static (SyncSchedulerWorker Worker, CicloStub Ciclo) Crear(bool runOnStartup)
    {
        var opciones = new SchedulerOptions
        {
            EveryMinutes = 30,
            ActiveFrom = "07:00",
            ActiveTo = "19:00",
            RunOnStartup = runOnStartup,
            ForceFilePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".txt"),
            ForcePollSeconds = 1,
        };

        var ciclo = new CicloStub();
        var worker = new SyncSchedulerWorker(
            ciclo,
            new ForceRequestWatcher(
                Options.Create(opciones), new FakeEnvironment(), NullLogger<ForceRequestWatcher>.Instance),
            Options.Create(opciones),
            TimeProvider.System,
            NullLogger<SyncSchedulerWorker>.Instance);

        return (worker, ciclo);
    }

    private sealed class CicloStub : ISyncCycle
    {
        public int Ejecuciones { get; private set; }

        /// <summary>Los pasos pedidos en la última ejecución, si se filtró.</summary>
        public IReadOnlyCollection<string>? UltimosPasosPedidos { get; private set; }

        public Task<SyncCycleResult> RunAsync(
            SyncCycleTrigger trigger,
            CancellationToken cancellationToken,
            IReadOnlyCollection<string>? soloPasos = null)
        {
            Ejecuciones++;
            UltimosPasosPedidos = soloPasos;
            return Task.FromResult(new SyncCycleResult(
                trigger, true, TimeSpan.Zero, 0, 0, Array.Empty<string>()));
        }
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "DinasWms.SapSync.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Testing";
    }
}
