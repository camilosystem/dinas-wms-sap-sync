using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// El disparo por archivo se verificó contra SAP en real; estas pruebas cubren
/// los casos que no se pueden provocar cómodamente así: el archivo que no se
/// puede borrar, y el archivo viejo que quedó de una corrida anterior.
/// </summary>
public sealed class ForceRequestWatcherTests : IDisposable
{
    private readonly string _carpeta;
    private readonly string _archivo;

    public ForceRequestWatcherTests()
    {
        _carpeta = Path.Combine(Path.GetTempPath(), "sapsync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_carpeta);
        _archivo = Path.Combine(_carpeta, "forzar-ahora.txt");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_carpeta, recursive: true);
        }
        catch
        {
            // Limpieza best-effort; no debe hacer fallar la prueba.
        }
    }

    private ForceRequestWatcher CrearWatcher() => new(
        Options.Create(new SchedulerOptions { ForceFilePath = _archivo }),
        new FakeEnvironment(),
        NullLogger<ForceRequestWatcher>.Instance);

    [Fact]
    public void Sin_archivo_no_hay_peticion()
    {
        Assert.False(CrearWatcher().TryConsumeRequest());
    }

    [Fact]
    public void Con_archivo_hay_peticion_y_se_borra()
    {
        File.WriteAllText(_archivo, "forzar");
        var watcher = CrearWatcher();

        Assert.True(watcher.TryConsumeRequest());
        Assert.False(File.Exists(_archivo));
    }

    [Fact]
    public void Una_peticion_se_atiende_una_sola_vez()
    {
        File.WriteAllText(_archivo, "forzar");
        var watcher = CrearWatcher();

        Assert.True(watcher.TryConsumeRequest());
        Assert.False(watcher.TryConsumeRequest());
    }

    [Fact]
    public void Un_archivo_bloqueado_no_dispara_en_bucle()
    {
        // Escenario real: el archivo quedó abierto en un editor y no se puede
        // borrar. Sin la guarda por marca de tiempo, el scheduler correría
        // ciclos contra SAP sin parar.
        File.WriteAllText(_archivo, "forzar");
        var watcher = CrearWatcher();

        using (File.Open(_archivo, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.True(watcher.TryConsumeRequest());   // se atiende
            Assert.True(File.Exists(_archivo));         // el borrado falló
            Assert.False(watcher.TryConsumeRequest());  // pero no se repite
            Assert.False(watcher.TryConsumeRequest());
        }
    }

    [Fact]
    public void Un_forzado_nuevo_despues_de_uno_atendido_si_dispara()
    {
        File.WriteAllText(_archivo, "forzar");
        var watcher = CrearWatcher();
        Assert.True(watcher.TryConsumeRequest());

        // Segunda petición, con marca de tiempo posterior.
        File.WriteAllText(_archivo, "forzar de nuevo");
        File.SetLastWriteTimeUtc(_archivo, DateTime.UtcNow.AddSeconds(1));

        Assert.True(watcher.TryConsumeRequest());
    }

    [Fact]
    public void Un_archivo_viejo_al_arrancar_se_descarta_sin_disparar()
    {
        // Un centinela de hace días no representa la intención de nadie hoy: un
        // reinicio del servicio no debe generar tráfico sorpresa contra SAP.
        File.WriteAllText(_archivo, "forzado viejo");
        File.SetLastWriteTimeUtc(_archivo, DateTime.UtcNow.AddDays(-3));

        var watcher = CrearWatcher();
        watcher.ClearStaleRequestAtStartup();

        Assert.False(File.Exists(_archivo));
        Assert.False(watcher.TryConsumeRequest());
    }

    [Fact]
    public void Ruta_relativa_se_resuelve_contra_el_content_root()
    {
        var watcher = new ForceRequestWatcher(
            Options.Create(new SchedulerOptions { ForceFilePath = "forzar-ahora.txt" }),
            new FakeEnvironment { ContentRootPath = _carpeta },
            NullLogger<ForceRequestWatcher>.Instance);

        Assert.Equal(Path.Combine(_carpeta, "forzar-ahora.txt"), watcher.FullPath);
    }

    private sealed class FakeEnvironment : IHostEnvironment
    {
        public string ApplicationName { get; set; } = "DinasWms.SapSync.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Testing";
    }
}
