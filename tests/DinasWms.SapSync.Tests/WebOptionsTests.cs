using DinasWms.SapSync.Configuration;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// La pantalla expone botones que escriben en SAP, así que dónde escucha no es
/// un detalle. Y el caso del array repetido no es hipotético: tumbó el arranque
/// la primera vez que se probó la rama web.
/// </summary>
public class WebOptionsTests
{
    private static WebOptions Bindear(Dictionary<string, string?> valores)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(valores).Build();
        var opciones = new WebOptions();
        config.GetSection(WebOptions.SectionName).Bind(opciones);
        return opciones;
    }

    [Fact]
    public void SinDireccionConfigurada_caeALoopback()
    {
        // El default seguro: si nadie dijo dónde escuchar, no se expone nada.
        var o = new WebOptions();
        o.Validate();

        Assert.Equal(["127.0.0.1"], o.BindAddresses);
    }

    [Fact]
    public void ElBinderConcatena_yLasRepetidasSeLimpian()
    {
        // .NET NO reemplaza arrays al bindear: los concatena a lo que el objeto
        // ya tenía. Con un default no vacío, un appsettings que repita loopback
        // deja la dirección dos veces y Kestrel falla con "address already in
        // use". Acá se fija que eso no vuelva a pasar.
        var o = Bindear(new Dictionary<string, string?>
        {
            ["Web:BindAddresses:0"] = "127.0.0.1",
            ["Web:BindAddresses:1"] = "100.126.181.94",
            ["Web:BindAddresses:2"] = "127.0.0.1",
        });

        o.Validate();

        Assert.Equal(2, o.BindAddresses.Length);
        Assert.Equal(2, o.BuildUrls().Distinct().Count());
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("*")]
    [InlineData("::")]
    public void ComodinNoSeAdmite(string comodin)
    {
        var o = new WebOptions { BindAddresses = [comodin] };

        var ex = Assert.Throws<InvalidOperationException>(o.Validate);
        Assert.Contains("comodines", ex.Message);
    }

    [Fact]
    public void DireccionQueNoEsIp_noArranca()
    {
        var o = new WebOptions { BindAddresses = ["localhost"] };

        Assert.Throws<InvalidOperationException>(o.Validate);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    public void PuertoFueraDeRango_noArranca(int puerto)
    {
        var o = new WebOptions { Port = puerto };

        var ex = Assert.Throws<InvalidOperationException>(o.Validate);
        Assert.Contains("Port", ex.Message);
    }

    [Fact]
    public void LasUrlsSonHttpConElPuerto()
    {
        var o = new WebOptions { Port = 5280, BindAddresses = ["127.0.0.1", "100.126.181.94"] };
        o.Validate();

        Assert.Equal(
            ["http://127.0.0.1:5280", "http://100.126.181.94:5280"],
            o.BuildUrls());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(301)]
    public void EsperaDeDireccionFueraDeRango_noArranca(int segundos)
    {
        // Esperar sirve para cubrir una carrera de segundos. Un valor absurdo
        // dejaría el arranque colgado, que no es mejor que fallar.
        var o = new WebOptions { WaitForAddressSeconds = segundos };

        var ex = Assert.Throws<InvalidOperationException>(o.Validate);
        Assert.Contains("WaitForAddressSeconds", ex.Message);
    }

    [Fact]
    public void DeshabilitadaPorDefecto()
    {
        // Que la web sea opt-in importa: los modos de diagnóstico y cualquier
        // despliegue que no la quiera no deben abrir un puerto por omisión.
        Assert.False(new WebOptions().Enabled);
    }
}
