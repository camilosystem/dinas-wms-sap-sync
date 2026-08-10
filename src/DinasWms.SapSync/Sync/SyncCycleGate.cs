using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Sync;

/// <summary>
/// Permiso único para correr un ciclo. Nadie abre sesión con SAP sin tenerlo.
/// </summary>
/// <remarks>
/// Hasta ahora el no-solapamiento era estructural: un solo bucle secuencial, así
/// que dos ciclos no podían pisarse. Esa garantía se cae en cuanto aparece una
/// segunda puerta — el botón de la interfaz de monitoreo, y ya existía una
/// tercera, el archivo centinela de "forzar ahora".
///
/// <para>
/// El portón convierte esa garantía en algo explícito y verificable: pasa de
/// "hay un solo bucle" a <b>"hay un solo permiso"</b>. Cualquier puerta nueva
/// que se agregue en el futuro tiene que pedirlo, y si no lo hace el problema es
/// visible en el código en vez de manifestarse como dos sesiones simultáneas
/// creando documentos duplicados en SAP.
/// </para>
/// <para>
/// <b>Rechaza en vez de encolar.</b> Si el permiso está tomado, el que llega
/// segundo se va con las manos vacías y quien lo pidió se entera en el momento.
/// Encolar significaría que alguien aprieta el botón, no ve nada, y tres minutos
/// después se ejecuta algo que ya no quería.
/// </para>
/// <para>
/// <b>Alcance deliberado:</b> el portón envuelve el ciclo que ESCRIBE en SAP, no
/// el sondeo de reposo. El sondeo solo le pregunta al middleware si hay tareas y
/// no toca Service Layer; si tomara el permiso cada 20 segundos, un disparo
/// manual podría chocar con un sondeo que no está haciendo nada y comerse un 409
/// fugaz sin razón.
/// </para>
/// </remarks>
public sealed class SyncCycleGate
{
    private readonly SemaphoreSlim _permiso = new(1, 1);
    private readonly ILogger<SyncCycleGate> _logger;
    private readonly object _candado = new();

    private string? _titular;
    private DateTimeOffset? _tomadoEn;

    public SyncCycleGate(ILogger<SyncCycleGate> logger) => _logger = logger;

    /// <summary>¿Hay un ciclo en curso?</summary>
    public bool EnUso
    {
        get
        {
            lock (_candado)
            {
                return _titular is not null;
            }
        }
    }

    /// <summary>Quién tiene el permiso ahora, y desde cuándo. Null si está libre.</summary>
    public (string Titular, DateTimeOffset Desde)? Ocupante
    {
        get
        {
            lock (_candado)
            {
                return _titular is null || _tomadoEn is null
                    ? null
                    : (_titular, _tomadoEn.Value);
            }
        }
    }

    /// <summary>
    /// Intenta tomar el permiso. Devuelve un objeto a liberar al terminar, o
    /// <c>null</c> si ya hay un ciclo en curso.
    /// </summary>
    /// <param name="titular">Quién lo pide, para el log y para la pantalla.</param>
    /// <param name="espera">
    /// Cuánto esperar antes de rendirse. Cero —el default— rechaza al instante,
    /// que es lo que se quiere: quien llega segundo prefiere enterarse ahora y
    /// reintentar, no quedarse colgado.
    /// </param>
    public async Task<IDisposable?> TryEnterAsync(
        string titular,
        TimeSpan espera = default,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(titular);

        var tomado = await _permiso.WaitAsync(espera, cancellationToken).ConfigureAwait(false);

        if (!tomado)
        {
            var ocupante = Ocupante;
            _logger.LogWarning(
                "{Titular} pidió correr un ciclo pero YA hay uno en curso{Detalle}. Se rechaza en " +
                "vez de encolar: nunca dos ciclos a la vez contra SAP.",
                titular,
                ocupante is null
                    ? ""
                    : $" ({ocupante.Value.Titular}, desde hace " +
                      $"{(DateTimeOffset.UtcNow - ocupante.Value.Desde).TotalSeconds:0}s)");
            return null;
        }

        lock (_candado)
        {
            _titular = titular;
            _tomadoEn = DateTimeOffset.UtcNow;
        }

        return new Permiso(this, titular);
    }

    private void Liberar()
    {
        lock (_candado)
        {
            _titular = null;
            _tomadoEn = null;
        }

        _permiso.Release();
    }

    /// <summary>
    /// El permiso tomado. Liberar dos veces no rompe: el semáforo se soltaría de
    /// más y dejaría entrar a dos, que es exactamente lo que esto evita.
    /// </summary>
    private sealed class Permiso : IDisposable
    {
        private readonly SyncCycleGate _porton;
        private readonly string _titular;
        private int _liberado;

        public Permiso(SyncCycleGate porton, string titular)
        {
            _porton = porton;
            _titular = titular;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _liberado, 1) == 0)
            {
                _porton.Liberar();
            }
            else
            {
                _porton._logger.LogWarning(
                    "El permiso de {Titular} se liberó dos veces. Se ignora la segunda.", _titular);
            }
        }
    }
}
