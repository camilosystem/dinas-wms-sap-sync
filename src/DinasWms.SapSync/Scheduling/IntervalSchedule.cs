namespace DinasWms.SapSync.Scheduling;

/// <summary>
/// Cálculo del próximo ciclo en modo intervalo. Función pura, sin dependencias
/// de reloj ni de configuración, para poder verificarla sin esperar en tiempo real.
/// </summary>
/// <remarks>
/// Los slots se alinean a la hora de inicio del horario activo, no al arranque
/// del proceso: con <c>07:00–19:00</c> cada 30 min los slots son 07:00, 07:30,
/// … 19:00 todos los días, sin importar a qué hora se reinició el servicio.
/// El slot que cae exactamente en la hora de fin sí se ejecuta.
///
/// Se trabaja en hora local (<see cref="DateTime"/> sin offset) porque el
/// horario lo define la operación de la oficina, que es la del reloj de esta
/// máquina. Nota: en una zona con horario de verano, los dos días de transición
/// tendrían un slot corrido; Colombia no aplica DST, así que hoy no aplica.
/// </remarks>
public static class IntervalSchedule
{
    /// <summary>
    /// Devuelve el primer slot estrictamente posterior a <paramref name="now"/>.
    /// </summary>
    /// <param name="now">Momento de referencia, en hora local.</param>
    /// <param name="activeFrom">Inicio del horario activo.</param>
    /// <param name="activeTo">Fin del horario activo (inclusive).</param>
    /// <param name="everyMinutes">Minutos entre slots.</param>
    /// <param name="maxDaysAhead">
    /// Tope de días a explorar. Solo es una red de seguridad: con una
    /// configuración válida el slot se encuentra hoy o mañana.
    /// </param>
    /// <returns>
    /// El próximo slot, o <c>null</c> si no se encontró ninguno dentro del tope
    /// (indicaría configuración inconsistente, no una condición normal).
    /// </returns>
    public static DateTime? GetNextRun(
        DateTime now,
        TimeOnly activeFrom,
        TimeOnly activeTo,
        int everyMinutes,
        int maxDaysAhead = 8)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(everyMinutes, 0);

        if (activeFrom >= activeTo)
        {
            throw new ArgumentException(
                $"activeFrom ({activeFrom}) debe ser anterior a activeTo ({activeTo}); " +
                "no se soportan ventanas que cruzan la medianoche.",
                nameof(activeFrom));
        }

        var intervalo = TimeSpan.FromMinutes(everyMinutes);

        for (var dia = 0; dia <= maxDaysAhead; dia++)
        {
            var fecha = now.Date.AddDays(dia);
            var inicioVentana = fecha.Add(activeFrom.ToTimeSpan());
            var finVentana = fecha.Add(activeTo.ToTimeSpan());

            // Iteración explícita en vez de aritmética "inteligente": la ventana
            // tiene decenas de slots como máximo, y así el comportamiento es
            // evidente al leerlo.
            for (var slot = inicioVentana; slot <= finVentana; slot = slot.Add(intervalo))
            {
                if (slot > now)
                {
                    return slot;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Indica si <paramref name="instant"/> cae dentro del horario activo.
    /// </summary>
    public static bool IsWithinActiveWindow(DateTime instant, TimeOnly activeFrom, TimeOnly activeTo)
    {
        var hora = TimeOnly.FromDateTime(instant);
        return hora >= activeFrom && hora <= activeTo;
    }

    /// <summary>
    /// Cuenta los slots que quedaron atrás sin ejecutarse entre dos momentos.
    /// Sirve para reportar cuando un ciclo se pasó de largo y se saltó ventanas.
    /// </summary>
    public static int CountMissedSlots(
        DateTime from,
        DateTime to,
        TimeOnly activeFrom,
        TimeOnly activeTo,
        int everyMinutes)
    {
        if (to <= from)
        {
            return 0;
        }

        var perdidos = 0;
        var cursor = from;

        while (true)
        {
            var siguiente = GetNextRun(cursor, activeFrom, activeTo, everyMinutes);
            if (siguiente is null || siguiente >= to)
            {
                return perdidos;
            }

            perdidos++;
            cursor = siguiente.Value;
        }
    }
}
