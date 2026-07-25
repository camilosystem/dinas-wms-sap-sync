using DinasWms.SapSync.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Sync;

/// <summary>
/// Detecta peticiones de "forzar ahora" por archivo centinela.
/// </summary>
/// <remarks>
/// Se eligió archivo centinela en vez de un endpoint HTTP porque funciona igual
/// corriendo como consola y como Windows Service, no agrega hosting web al
/// proyecto, y no depende del middleware (cuyo consumo es una fase posterior).
/// Se dispara con un doble clic, un .bat, o una tarea programada.
///
/// Se usa polling y no FileSystemWatcher: el ciclo de espera del scheduler ya
/// hace polling de todas formas, y FileSystemWatcher pierde eventos y se
/// comporta mal en rutas de red.
/// </remarks>
public sealed class ForceRequestWatcher
{
    private readonly ILogger<ForceRequestWatcher> _logger;

    /// <summary>
    /// Marca de tiempo de la última petición atendida. Es la defensa real contra
    /// disparar dos veces: si el borrado del archivo falla (archivo abierto en un
    /// editor, permisos), esto evita que el ciclo se repita en bucle.
    /// </summary>
    private DateTime _ultimaAtendidaUtc = DateTime.MinValue;

    public ForceRequestWatcher(
        IOptions<SchedulerOptions> options,
        IHostEnvironment environment,
        ILogger<ForceRequestWatcher> logger)
    {
        _logger = logger;

        var configurada = options.Value.ForceFilePath;
        FullPath = Path.IsPathRooted(configurada)
            ? configurada
            : Path.Combine(environment.ContentRootPath, configurada);
    }

    /// <summary>Ruta absoluta del archivo centinela.</summary>
    public string FullPath { get; }

    /// <summary>
    /// Descarta un archivo centinela que haya quedado de una ejecución anterior.
    /// </summary>
    /// <remarks>
    /// A propósito NO dispara un ciclo: un archivo viejo de hace días no
    /// representa la intención de nadie hoy, y no queremos que un reinicio del
    /// servicio genere tráfico sorpresa contra SAP. Se borra y se avisa.
    /// </remarks>
    public void ClearStaleRequestAtStartup()
    {
        try
        {
            if (!File.Exists(FullPath))
            {
                return;
            }

            var escrito = File.GetLastWriteTime(FullPath);
            _logger.LogWarning(
                "Al arrancar ya existía el archivo de forzado {Path} (del {Escrito:yyyy-MM-dd HH:mm:ss}). " +
                "Se descarta sin correr un ciclo. Si querías forzar ahora, créalo de nuevo.",
                FullPath,
                escrito);

            File.Delete(FullPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "No se pudo revisar/limpiar el archivo de forzado {Path}.", FullPath);
        }
    }

    /// <summary>
    /// Devuelve true si hay una petición de forzado pendiente, y la marca como
    /// atendida. El borrado del archivo es best-effort.
    /// </summary>
    public bool TryConsumeRequest()
    {
        try
        {
            if (!File.Exists(FullPath))
            {
                return false;
            }

            var escritoUtc = File.GetLastWriteTimeUtc(FullPath);
            if (escritoUtc <= _ultimaAtendidaUtc)
            {
                // Ya se atendió esta petición; el archivo sigue ahí porque no se
                // pudo borrar. No se vuelve a disparar.
                return false;
            }

            _ultimaAtendidaUtc = escritoUtc;

            _logger.LogInformation(
                "Petición de forzado detectada en {Path}. Se corre un ciclo fuera de horario.",
                FullPath);

            try
            {
                File.Delete(FullPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Se atendió el forzado pero no se pudo borrar {Path}. No se volverá a disparar " +
                    "por este archivo salvo que se modifique de nuevo.",
                    FullPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            // Nunca dejar que un problema de archivo tumbe el scheduler.
            _logger.LogWarning(ex, "Error revisando el archivo de forzado {Path}.", FullPath);
            return false;
        }
    }
}
