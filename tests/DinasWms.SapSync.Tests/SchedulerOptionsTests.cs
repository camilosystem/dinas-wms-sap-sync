using DinasWms.SapSync.Configuration;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// La validación de configuración importa: un horario mal escrito que arranque
/// "callado" sincronizaría con SAP a horas que nadie pretendía.
/// </summary>
public class SchedulerOptionsTests
{
    private static SchedulerOptions Valida() => new()
    {
        EveryMinutes = 30,
        ActiveFrom = "07:00",
        ActiveTo = "19:00",
        ForceFilePath = "forzar-ahora.txt",
        ForcePollSeconds = 5,
    };

    [Fact]
    public void Configuracion_valida_parsea_las_horas()
    {
        var options = Valida();
        options.Validate();

        Assert.Equal(new TimeOnly(7, 0), options.ParsedActiveFrom);
        Assert.Equal(new TimeOnly(19, 0), options.ParsedActiveTo);
    }

    [Theory]
    [InlineData("7:00")]
    [InlineData("07:00:00")]
    [InlineData("7am")]
    [InlineData("25:00")]
    [InlineData("")]
    public void Hora_con_formato_invalido_no_arranca(string hora)
    {
        var options = Valida();
        options.ActiveFrom = hora;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("HH:mm", ex.Message);
    }

    [Fact]
    public void Ventana_que_cruza_medianoche_se_rechaza_con_mensaje_claro()
    {
        var options = Valida();
        options.ActiveFrom = "19:00";
        options.ActiveTo = "07:00";

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("medianoche", ex.Message);
    }

    [Fact]
    public void Intervalo_mayor_que_la_ventana_se_rechaza()
    {
        // 07:00–08:00 cada 120 min solo correría el primer slot del día, que
        // casi seguro no es lo que se quiso configurar.
        var options = Valida();
        options.ActiveTo = "08:00";
        options.EveryMinutes = 120;

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("ventana", ex.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Intervalo_no_positivo_se_rechaza(int minutos)
    {
        var options = Valida();
        options.EveryMinutes = minutos;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Archivo_de_forzado_vacio_se_rechaza()
    {
        var options = Valida();
        options.ForceFilePath = "   ";

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Intervalo_de_revision_no_positivo_se_rechaza()
    {
        var options = Valida();
        options.ForcePollSeconds = 0;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Defaults_del_appsettings_son_validos()
    {
        // Los defaults de la clase deben coincidir con lo versionado en
        // appsettings.json y pasar validación tal cual.
        var options = new SchedulerOptions();
        options.Validate();

        Assert.Equal(30, options.EveryMinutes);
        Assert.Equal(new TimeOnly(7, 0), options.ParsedActiveFrom);
        Assert.Equal(new TimeOnly(19, 0), options.ParsedActiveTo);
        Assert.False(options.RunOnStartup);
    }
}
