using DinasWms.SapSync.Sync;

namespace DinasWms.SapSync.Observability;

/// <summary>
/// Estado en vivo del sincronizador, para que la pantalla lo lea de memoria.
/// </summary>
/// <remarks>
/// <b>Nada de esto existía.</b> El contador de fallos consecutivos era una
/// variable local del bucle, los sondeos se contaban grepeando el log, y si
/// había sesión de SAP abierta no lo sabía nadie: la sesión es un
/// <c>await using</c> local dentro del ciclo. Toda esta clase es instrumentación
/// nueva.
///
/// <para>
/// Se escribe desde el bucle del worker y se lee desde requests HTTP, así que
/// los contadores son <see cref="Interlocked"/> y las referencias se publican
/// con <see cref="Volatile"/>. No hace falta un lock: cada campo se actualiza
/// solo, nadie necesita ver dos campos consistentes entre sí en el mismo
/// instante, y una pantalla que muestre el contador de sondeos un tick viejo no
/// le hace daño a nadie.
/// </para>
/// </remarks>
public sealed class SyncStatus
{
    private long _sondeos;
    private long _ciclos;
    private long _documentosIntegrados;
    private long _documentosFallidos;
    private int _fallosConsecutivos;
    private int _sesionSapAbierta;

    // Las marcas de tiempo van bajo lock y no con Volatile: DateTimeOffset? es
    // un struct y Volatile solo admite tipos por referencia. El lock es corto y
    // se toma pocas veces por minuto.
    private readonly object _candadoFechas = new();
    private DateTimeOffset? _ultimoSondeo;
    private DateTimeOffset? _ultimoCiclo;
    private DateTimeOffset? _proximoIntento;

    private CicloTerminado? _ultimoResultado;

    public SyncStatus(TimeProvider timeProvider) => IniciadoEn = timeProvider.GetLocalNow();

    /// <summary>Cuándo arrancó el proceso.</summary>
    public DateTimeOffset IniciadoEn { get; }

    /// <summary>Modo de ejecución en curso (Continuous, Scheduler…).</summary>
    public string Modo { get; set; } = "(sin definir)";

    /// <summary>Cadencia vigente en segundos. Cambia si se ajusta la config.</summary>
    public int CadenciaSegundos { get; set; }

    /// <summary>Pasos que corren automáticos, por nombre.</summary>
    public IReadOnlyList<string> PasosAutomaticos { get; set; } = [];

    public long Sondeos => Interlocked.Read(ref _sondeos);

    public long Ciclos => Interlocked.Read(ref _ciclos);

    public long DocumentosIntegrados => Interlocked.Read(ref _documentosIntegrados);

    public long DocumentosFallidos => Interlocked.Read(ref _documentosFallidos);

    public int FallosConsecutivos => Volatile.Read(ref _fallosConsecutivos);

    /// <summary>
    /// ¿Hay una sesión de Service Layer abierta en este momento? Es el dato que
    /// dice si el sincronizador está consumiendo una licencia AHORA.
    /// </summary>
    public bool SesionSapAbierta => Volatile.Read(ref _sesionSapAbierta) != 0;

    public DateTimeOffset? UltimoSondeo
    {
        get { lock (_candadoFechas) { return _ultimoSondeo; } }
    }

    public DateTimeOffset? UltimoCiclo
    {
        get { lock (_candadoFechas) { return _ultimoCiclo; } }
    }

    /// <summary>Cuándo se espera el próximo sondeo. Se ensancha con el back-off.</summary>
    public DateTimeOffset? ProximoIntento
    {
        get { lock (_candadoFechas) { return _proximoIntento; } }
    }

    public CicloTerminado? UltimoResultado => Volatile.Read(ref _ultimoResultado);

    public void RegistrarSondeo(DateTimeOffset cuando, DateTimeOffset proximo)
    {
        Interlocked.Increment(ref _sondeos);

        lock (_candadoFechas)
        {
            _ultimoSondeo = cuando;
            _proximoIntento = proximo;
        }
    }

    public void RegistrarSesionAbierta() => Volatile.Write(ref _sesionSapAbierta, 1);

    public void RegistrarSesionCerrada() => Volatile.Write(ref _sesionSapAbierta, 0);

    /// <summary>
    /// Historial en disco. Opcional para no obligar a las pruebas a crear un
    /// archivo, pero en producción siempre está.
    /// </summary>
    public Persistence.LocalStore? Historial { get; set; }

    public void RegistrarCicloTerminado(DateTimeOffset cuando, SyncCycleResult resultado)
    {
        // Un ciclo rechazado por el portón no cuenta: no corrió.
        if (resultado.RejectedByConcurrency)
        {
            return;
        }

        // Hoy cada ciclo dejaba su línea en el log y se olvidaba; sin esto no
        // hay forma de ver tendencias de tiempos.
        Historial?.RegistrarCiclo(
            cuando,
            resultado.Trigger.ToString(),
            resultado.Success,
            resultado.Duration,
            resultado.TotalProcessed,
            resultado.TotalFailed,
            resultado.ErrorMessage);

        Interlocked.Increment(ref _ciclos);
        Interlocked.Add(ref _documentosIntegrados, resultado.TotalProcessed);
        Interlocked.Add(ref _documentosFallidos, resultado.TotalFailed);

        lock (_candadoFechas)
        {
            _ultimoCiclo = cuando;
        }

        Volatile.Write(ref _ultimoResultado, new CicloTerminado(
            cuando,
            resultado.Trigger.ToString(),
            resultado.Success,
            resultado.Duration,
            resultado.TotalProcessed,
            resultado.TotalFailed,
            resultado.ErrorMessage));
    }

    public void RegistrarFallosConsecutivos(int fallos) =>
        Volatile.Write(ref _fallosConsecutivos, fallos);
}

/// <summary>Resumen del último ciclo, para la pantalla.</summary>
public sealed record CicloTerminado(
    DateTimeOffset Cuando,
    string Disparo,
    bool Exito,
    TimeSpan Duracion,
    int Integrados,
    int Fallidos,
    string? Error);
