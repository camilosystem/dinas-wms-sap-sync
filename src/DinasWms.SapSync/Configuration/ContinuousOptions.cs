namespace DinasWms.SapSync.Configuration;

/// <summary>
/// Configuración del modo continuo: sondear seguido, trabajar solo si hay algo.
/// </summary>
/// <remarks>
/// Reemplaza a la ventana horaria para los tipos automáticos. La idea de fondo:
/// preguntarle al middleware si hay trabajo es barato, abrir sesión con SAP no
/// lo es. Sondeando cada pocos segundos y abriendo sesión solo cuando hay
/// tareas, una factura confirmada aparece en SAP en segundos sin gastar una sola
/// licencia de más mientras no pasa nada.
/// </remarks>
public sealed class ContinuousOptions
{
    public const string SectionName = "Continuous";

    /// <summary>Segundos entre sondeos cuando todo va bien.</summary>
    public int PollSeconds { get; set; } = 20;

    /// <summary>
    /// Tope del back-off. Tras fallos consecutivos el intervalo se duplica hasta
    /// acá; con los valores por defecto son 20s → 40 → 80 → 160 → 300.
    /// </summary>
    public int MaxBackoffSeconds { get; set; } = 300;

    /// <summary>
    /// Cuántos fallos consecutivos hacen falta antes de empezar a ensanchar. Con
    /// 1, un fallo aislado ya duplica el intervalo; con 2 se tolera un tropiezo
    /// suelto sin perder cadencia.
    /// </summary>
    public int FailuresBeforeBackoff { get; set; } = 2;

    /// <summary>
    /// Si es true, corre un ciclo al arrancar aunque no haya nada. Default false:
    /// un reinicio no debería generar tráfico contra SAP por sí solo.
    /// </summary>
    public bool RunOnStartup { get; set; }

    public void Validate()
    {
        if (PollSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(PollSeconds)} debe ser mayor que 0 (actual: {PollSeconds}).");
        }

        if (MaxBackoffSeconds < PollSeconds)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(MaxBackoffSeconds)} ({MaxBackoffSeconds}) no puede ser menor " +
                $"que {nameof(PollSeconds)} ({PollSeconds}): el back-off solo ensancha, nunca acorta.");
        }

        if (FailuresBeforeBackoff <= 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(FailuresBeforeBackoff)} debe ser mayor que 0 " +
                $"(actual: {FailuresBeforeBackoff}).");
        }
    }

    /// <summary>
    /// Cuánto esperar tras <paramref name="fallosConsecutivos"/> fallos seguidos.
    /// </summary>
    /// <remarks>
    /// Se separa del worker para poder probarla sin relojes ni red: es la clase
    /// de lógica que da vergüenza tener mal y no se ve en una corrida feliz.
    /// </remarks>
    public TimeSpan CalcularEspera(int fallosConsecutivos)
    {
        if (fallosConsecutivos < FailuresBeforeBackoff)
        {
            return TimeSpan.FromSeconds(PollSeconds);
        }

        // Duplicación por cada fallo pasado el umbral, con tope. El exponente se
        // limita para que no desborde con rachas largas.
        var pasos = Math.Min(fallosConsecutivos - FailuresBeforeBackoff + 1, 20);
        var segundos = (double)PollSeconds * Math.Pow(2, pasos);

        return TimeSpan.FromSeconds(Math.Min(segundos, MaxBackoffSeconds));
    }
}
