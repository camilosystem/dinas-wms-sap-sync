using DinasWms.SapSync.Configuration;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// El back-off es lo que separa "SAP está caído y el log lo dice una vez cada
/// tanto" de "SAP está caído y el log tiene 3.000 líneas iguales que tapan el
/// problema". No se ve en una corrida feliz, así que se prueba acá.
/// </summary>
public class ContinuousOptionsTests
{
    private static ContinuousOptions Opciones() => new()
    {
        PollSeconds = 20,
        MaxBackoffSeconds = 300,
        FailuresBeforeBackoff = 2,
    };

    [Fact]
    public void SinFallos_manteneLaCadenciaNormal()
    {
        Assert.Equal(TimeSpan.FromSeconds(20), Opciones().CalcularEspera(0));
    }

    [Fact]
    public void UnFalloSuelto_noEnsanchaTodavia()
    {
        // Con el umbral en 2, un tropiezo aislado no debe costar cadencia: una
        // factura confirmada justo después seguiría saliendo en 20 segundos.
        Assert.Equal(TimeSpan.FromSeconds(20), Opciones().CalcularEspera(1));
    }

    [Fact]
    public void DesdeElUmbral_duplicaPorCadaFallo()
    {
        var o = Opciones();

        Assert.Equal(TimeSpan.FromSeconds(40), o.CalcularEspera(2));
        Assert.Equal(TimeSpan.FromSeconds(80), o.CalcularEspera(3));
        Assert.Equal(TimeSpan.FromSeconds(160), o.CalcularEspera(4));
    }

    [Fact]
    public void NuncaPasaDelTope()
    {
        var o = Opciones();

        Assert.Equal(TimeSpan.FromSeconds(300), o.CalcularEspera(5));
        Assert.Equal(TimeSpan.FromSeconds(300), o.CalcularEspera(50));
    }

    [Fact]
    public void UnaRachaLarguisima_noDesborda()
    {
        // Sin el tope del exponente, 2^1000 daría infinito y el TimeSpan
        // reventaría. Una caída de fin de semana llega a números así.
        var espera = Opciones().CalcularEspera(int.MaxValue);

        Assert.Equal(TimeSpan.FromSeconds(300), espera);
    }

    [Fact]
    public void TrasRecuperarse_vuelveALaCadenciaNormal()
    {
        // El worker resetea el contador a 0 en el primer éxito; esto fija que ese
        // 0 signifique de verdad "como al principio".
        var o = Opciones();
        _ = o.CalcularEspera(9);

        Assert.Equal(TimeSpan.FromSeconds(20), o.CalcularEspera(0));
    }

    [Fact]
    public void PollSecondsNoPositivo_noArranca()
    {
        var o = Opciones();
        o.PollSeconds = 0;

        var ex = Assert.Throws<InvalidOperationException>(o.Validate);
        Assert.Contains("PollSeconds", ex.Message);
    }

    [Fact]
    public void TopeMenorQueLaCadencia_noArranca()
    {
        // Un tope por debajo del intervalo normal significaría que el back-off
        // ACORTA la espera ante fallos, que es exactamente lo contrario.
        var o = Opciones();
        o.MaxBackoffSeconds = 10;

        var ex = Assert.Throws<InvalidOperationException>(o.Validate);
        Assert.Contains("MaxBackoffSeconds", ex.Message);
    }

    [Fact]
    public void UmbralNoPositivo_noArranca()
    {
        var o = Opciones();
        o.FailuresBeforeBackoff = 0;

        var ex = Assert.Throws<InvalidOperationException>(o.Validate);
        Assert.Contains("FailuresBeforeBackoff", ex.Message);
    }

    [Fact]
    public void ConUmbralEnUno_elPrimerFalloYaEnsancha()
    {
        var o = Opciones();
        o.FailuresBeforeBackoff = 1;

        Assert.Equal(TimeSpan.FromSeconds(20), o.CalcularEspera(0));
        Assert.Equal(TimeSpan.FromSeconds(40), o.CalcularEspera(1));
    }
}
