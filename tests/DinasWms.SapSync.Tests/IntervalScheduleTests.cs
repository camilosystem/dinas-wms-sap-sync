using DinasWms.SapSync.Scheduling;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// Verificación de la aritmética de slots. Es lo único del scheduler que no se
/// puede comprobar con una corrida real sin esperar horas o días.
/// </summary>
public class IntervalScheduleTests
{
    private static readonly TimeOnly Desde = new(7, 0);
    private static readonly TimeOnly Hasta = new(19, 0);
    private const int CadaMinutos = 30;

    private static DateTime? Proximo(DateTime ahora) =>
        IntervalSchedule.GetNextRun(ahora, Desde, Hasta, CadaMinutos);

    [Fact]
    public void Antes_de_abrir_la_ventana_devuelve_el_inicio_de_hoy()
    {
        var ahora = new DateTime(2026, 7, 24, 3, 15, 0);
        Assert.Equal(new DateTime(2026, 7, 24, 7, 0, 0), Proximo(ahora));
    }

    [Fact]
    public void Justo_en_un_slot_devuelve_el_siguiente_no_el_mismo()
    {
        // Importante: si devolviera el mismo slot, el bucle del scheduler
        // ejecutaría dos veces seguidas sin esperar.
        var ahora = new DateTime(2026, 7, 24, 8, 30, 0);
        Assert.Equal(new DateTime(2026, 7, 24, 9, 0, 0), Proximo(ahora));
    }

    [Fact]
    public void Entre_dos_slots_devuelve_el_siguiente()
    {
        var ahora = new DateTime(2026, 7, 24, 8, 44, 59);
        Assert.Equal(new DateTime(2026, 7, 24, 9, 0, 0), Proximo(ahora));
    }

    [Fact]
    public void Los_slots_se_alinean_al_inicio_de_la_ventana_no_al_arranque()
    {
        // Arrancar a las 07:05 no debe generar slots 07:35, 08:05, …
        var ahora = new DateTime(2026, 7, 24, 7, 5, 0);
        Assert.Equal(new DateTime(2026, 7, 24, 7, 30, 0), Proximo(ahora));
    }

    [Fact]
    public void Despues_de_cerrar_la_ventana_salta_al_dia_siguiente()
    {
        var ahora = new DateTime(2026, 7, 24, 20, 10, 0);
        Assert.Equal(new DateTime(2026, 7, 25, 7, 0, 0), Proximo(ahora));
    }

    [Fact]
    public void Exactamente_en_el_cierre_salta_al_dia_siguiente()
    {
        var ahora = new DateTime(2026, 7, 24, 19, 0, 0);
        Assert.Equal(new DateTime(2026, 7, 25, 7, 0, 0), Proximo(ahora));
    }

    [Fact]
    public void El_slot_que_cae_exacto_en_el_cierre_si_se_ejecuta()
    {
        var ahora = new DateTime(2026, 7, 24, 18, 45, 0);
        Assert.Equal(new DateTime(2026, 7, 24, 19, 0, 0), Proximo(ahora));
    }

    [Fact]
    public void Intervalo_que_no_divide_la_ventana_no_pasa_del_cierre()
    {
        // 07:00–19:00 cada 50 min: el último slot cabe a las 18:40; 19:00 no es
        // múltiplo, así que no debe inventarse un slot fuera de la ventana.
        var ahora = new DateTime(2026, 7, 24, 18, 41, 0);
        var proximo = IntervalSchedule.GetNextRun(ahora, Desde, Hasta, everyMinutes: 50);
        Assert.Equal(new DateTime(2026, 7, 25, 7, 0, 0), proximo);
    }

    [Fact]
    public void Ventana_de_dia_completo_pasa_a_medianoche()
    {
        // 00:00–23:59 es la forma de cubrir el día entero (no se soportan
        // ventanas que cruzan la medianoche). A las 23:30 el siguiente slot ya
        // es el primero del día siguiente.
        var proximo = IntervalSchedule.GetNextRun(
            new DateTime(2026, 7, 24, 23, 30, 0), new TimeOnly(0, 0), new TimeOnly(23, 59), 30);

        Assert.Equal(new DateTime(2026, 7, 25, 0, 0, 0), proximo);
    }

    [Fact]
    public void Cruza_el_fin_de_mes_correctamente()
    {
        var ahora = new DateTime(2026, 7, 31, 19, 30, 0);
        Assert.Equal(new DateTime(2026, 8, 1, 7, 0, 0), Proximo(ahora));
    }

    [Theory]
    [InlineData(6, 59, false)]
    [InlineData(7, 0, true)]
    [InlineData(13, 0, true)]
    [InlineData(19, 0, true)]
    [InlineData(19, 1, false)]
    public void IsWithinActiveWindow_respeta_los_bordes(int hora, int minuto, bool esperado)
    {
        var instante = new DateTime(2026, 7, 24, hora, minuto, 0);
        Assert.Equal(esperado, IntervalSchedule.IsWithinActiveWindow(instante, Desde, Hasta));
    }

    [Fact]
    public void CountMissedSlots_cuenta_los_slots_que_paso_de_largo_un_ciclo_lento()
    {
        // Ciclo que arrancó a las 08:00 y terminó 09:05: se saltaron 08:30 y 09:00.
        var perdidos = IntervalSchedule.CountMissedSlots(
            new DateTime(2026, 7, 24, 8, 0, 0),
            new DateTime(2026, 7, 24, 9, 5, 0),
            Desde,
            Hasta,
            CadaMinutos);

        Assert.Equal(2, perdidos);
    }

    [Fact]
    public void CountMissedSlots_es_cero_cuando_el_ciclo_cabe_en_el_intervalo()
    {
        var perdidos = IntervalSchedule.CountMissedSlots(
            new DateTime(2026, 7, 24, 8, 0, 0),
            new DateTime(2026, 7, 24, 8, 2, 30),
            Desde,
            Hasta,
            CadaMinutos);

        Assert.Equal(0, perdidos);
    }

    [Fact]
    public void Intervalo_no_positivo_es_error_de_programacion()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => IntervalSchedule.GetNextRun(new DateTime(2026, 7, 24, 8, 0, 0), Desde, Hasta, 0));
    }

    [Fact]
    public void Ventana_invertida_es_error_de_programacion()
    {
        Assert.Throws<ArgumentException>(
            () => IntervalSchedule.GetNextRun(
                new DateTime(2026, 7, 24, 8, 0, 0), new TimeOnly(19, 0), new TimeOnly(7, 0), 30));
    }
}
