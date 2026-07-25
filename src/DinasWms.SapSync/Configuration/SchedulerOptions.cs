using System.Globalization;

namespace DinasWms.SapSync.Configuration;

/// <summary>
/// Configuración del scheduler de ventanas de sincronización.
/// </summary>
/// <remarks>
/// Modo intervalo: se corre un ciclo cada <see cref="EveryMinutes"/> minutos,
/// pero solo dentro del horario activo <see cref="ActiveFrom"/>–<see cref="ActiveTo"/>.
/// Los slots se alinean a <see cref="ActiveFrom"/> (no al momento de arranque del
/// proceso), para que los ciclos caigan siempre en las mismas horas del día sin
/// importar cuándo se reinició el servicio.
/// </remarks>
public sealed class SchedulerOptions
{
    public const string SectionName = "Scheduler";

    /// <summary>Minutos entre ciclos dentro del horario activo.</summary>
    public int EveryMinutes { get; set; } = 30;

    /// <summary>Hora de inicio del horario activo, formato HH:mm.</summary>
    public string ActiveFrom { get; set; } = "07:00";

    /// <summary>Hora de fin del horario activo, formato HH:mm.</summary>
    public string ActiveTo { get; set; } = "19:00";

    /// <summary>
    /// Si es true, se corre un ciclo inmediatamente al arrancar en vez de esperar
    /// el primer slot. Default false: un reinicio del servicio no debería
    /// disparar tráfico contra SAP por sí solo.
    /// </summary>
    public bool RunOnStartup { get; set; }

    /// <summary>
    /// Archivo centinela para "forzar ahora". Si aparece, se corre un ciclo
    /// fuera de horario. Ruta relativa al content root, o absoluta.
    /// </summary>
    public string ForceFilePath { get; set; } = "forzar-ahora.txt";

    /// <summary>Cada cuántos segundos se revisa el archivo centinela.</summary>
    public int ForcePollSeconds { get; set; } = 5;

    /// <summary>Hora de inicio ya parseada. Válida después de <see cref="Validate"/>.</summary>
    public TimeOnly ParsedActiveFrom { get; private set; }

    /// <summary>Hora de fin ya parseada. Válida después de <see cref="Validate"/>.</summary>
    public TimeOnly ParsedActiveTo { get; private set; }

    /// <summary>
    /// Valida y parsea la configuración. Lanza si algo no cuadra — es preferible
    /// no arrancar que correr con un horario que no es el que se pretendía.
    /// </summary>
    public void Validate()
    {
        if (EveryMinutes <= 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(EveryMinutes)} debe ser mayor que 0 (actual: {EveryMinutes}).");
        }

        ParsedActiveFrom = ParseHora(ActiveFrom, nameof(ActiveFrom));
        ParsedActiveTo = ParseHora(ActiveTo, nameof(ActiveTo));

        if (ParsedActiveFrom >= ParsedActiveTo)
        {
            // Limitación conocida: no se soportan ventanas que cruzan la
            // medianoche (ej. 19:00–07:00). Para cubrir el día completo usar
            // 00:00–23:59. Si en algún momento se necesita una ventana nocturna,
            // hay que extender IntervalSchedule, no solo esta validación.
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(ActiveFrom)} ('{ActiveFrom}') debe ser anterior a " +
                $"{nameof(ActiveTo)} ('{ActiveTo}'). No se soportan ventanas que cruzan la " +
                "medianoche; para el día completo usar 00:00 – 23:59.");
        }

        var duracionVentana = ParsedActiveTo - ParsedActiveFrom;
        if (TimeSpan.FromMinutes(EveryMinutes) > duracionVentana)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(EveryMinutes)} ({EveryMinutes} min) es mayor que la ventana " +
                $"activa ({duracionVentana.TotalMinutes:0} min, {ActiveFrom}–{ActiveTo}). " +
                "Así solo correría el primer slot de cada día, que probablemente no es la intención.");
        }

        if (string.IsNullOrWhiteSpace(ForceFilePath))
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(ForceFilePath)} no puede estar vacío.");
        }

        if (ForcePollSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(ForcePollSeconds)} debe ser mayor que 0 (actual: {ForcePollSeconds}).");
        }
    }

    private static TimeOnly ParseHora(string valor, string nombreCampo)
    {
        if (TimeOnly.TryParseExact(valor, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var hora))
        {
            return hora;
        }

        throw new InvalidOperationException(
            $"{SectionName}:{nombreCampo} debe tener formato HH:mm en 24 horas (actual: '{valor}').");
    }
}
