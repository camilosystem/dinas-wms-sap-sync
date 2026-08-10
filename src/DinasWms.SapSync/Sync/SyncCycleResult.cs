namespace DinasWms.SapSync.Sync;

/// <summary>Qué disparó un ciclo. Solo afecta el log, no la lógica.</summary>
public enum SyncCycleTrigger
{
    /// <summary>Slot del horario configurado.</summary>
    Scheduled,

    /// <summary>Archivo centinela de "forzar ahora".</summary>
    Forced,

    /// <summary>Arranque del proceso, con RunOnStartup activo.</summary>
    Startup,
}

/// <summary>Resultado de un ciclo completo (login → pasos → logout).</summary>
public sealed record SyncCycleResult(
    SyncCycleTrigger Trigger,
    bool Success,
    TimeSpan Duration,
    int TotalProcessed,
    int TotalFailed,
    IReadOnlyList<string> StepSummaries,
    string? ErrorMessage = null,
    bool RejectedByConcurrency = false)
{
    public static SyncCycleResult Failure(
        SyncCycleTrigger trigger,
        TimeSpan duration,
        string errorMessage) =>
        new(trigger, false, duration, 0, 0, Array.Empty<string>(), errorMessage);

    /// <summary>
    /// No corrió porque ya había un ciclo en curso.
    /// </summary>
    /// <remarks>
    /// Se distingue de un fallo a propósito: que el portón rechace no significa
    /// que algo esté roto, significa que el sistema está haciendo justo lo que
    /// debe. Contarlo como fallo dispararía el back-off y ensancharía el
    /// intervalo por una razón sana, que es la clase de degradación silenciosa
    /// que después nadie entiende.
    /// </remarks>
    public static SyncCycleResult Rejected(SyncCycleTrigger trigger, string detalle) =>
        new(trigger, false, TimeSpan.Zero, 0, 0, Array.Empty<string>(), detalle, true);
}
