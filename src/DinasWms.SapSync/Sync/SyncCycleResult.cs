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
    string? ErrorMessage = null)
{
    public static SyncCycleResult Failure(
        SyncCycleTrigger trigger,
        TimeSpan duration,
        string errorMessage) =>
        new(trigger, false, duration, 0, 0, Array.Empty<string>(), errorMessage);
}
