using DinasWms.SapSync.Configuration;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DinasWms.SapSync.Tests;

public class SqlOptionsTests
{
    private static SqlOptions Valida() => new()
    {
        Server = "192.168.11.200",
        Database = "SUPPORT_DINAS",
        UserName = "wms_reader",
        Password = "clave",
    };

    [Fact]
    public void Arma_la_cadena_con_los_valores_configurados()
    {
        var cadena = Valida().BuildConnectionString();
        var leida = new SqlConnectionStringBuilder(cadena);

        Assert.Equal("192.168.11.200", leida.DataSource);
        Assert.Equal("SUPPORT_DINAS", leida.InitialCatalog);
        Assert.Equal("wms_reader", leida.UserID);
        Assert.False(leida.IntegratedSecurity);
        Assert.True(leida.Encrypt);
        Assert.True(leida.TrustServerCertificate);
    }

    [Theory]
    [InlineData("clave;con;puntoycoma")]
    [InlineData("clave'con'apostrofes")]
    [InlineData("clave=con=iguales")]
    [InlineData("clave\"con\"comillas")]
    [InlineData("cl{av}e[con]llaves")]
    [InlineData("Admin25*")]
    [InlineData("con espacios al final   ")]
    public void Una_contrasena_con_caracteres_especiales_sobrevive_intacta(string password)
    {
        // Por esto se usa SqlConnectionStringBuilder y no concatenación: una
        // cadena armada a mano con estos caracteres se rompería, o peor, se
        // alteraría en silencio y el fallo aparecería como "login failed".
        var options = Valida();
        options.Password = password;

        var leida = new SqlConnectionStringBuilder(options.BuildConnectionString());

        Assert.Equal(password, leida.Password);
    }

    [Fact]
    public void La_cadena_no_activa_autenticacion_integrada()
    {
        // Autenticación integrada usaría la cuenta de Windows de esta máquina,
        // que ya comprobamos que no tiene acceso a SUPPORT_DINAS.
        Assert.False(new SqlConnectionStringBuilder(Valida().BuildConnectionString()).IntegratedSecurity);
    }

    [Theory]
    [InlineData("", "SUPPORT_DINAS", "wms_reader", "clave")]
    [InlineData("srv", "", "wms_reader", "clave")]
    [InlineData("srv", "SUPPORT_DINAS", "", "clave")]
    [InlineData("srv", "SUPPORT_DINAS", "wms_reader", "")]
    public void Falta_algun_dato_obligatorio_y_no_arranca(
        string server, string database, string user, string password)
    {
        var options = new SqlOptions
        {
            Server = server,
            Database = database,
            UserName = user,
            Password = password,
        };

        var ex = Assert.Throws<InvalidOperationException>(options.Validate);
        Assert.Contains("user-secrets", ex.Message);
    }

    [Fact]
    public void Timeouts_no_positivos_se_rechazan()
    {
        var options = Valida();
        options.CommandTimeoutSeconds = 0;
        Assert.Throws<InvalidOperationException>(options.Validate);

        options = Valida();
        options.ConnectTimeoutSeconds = -1;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public void Defaults_del_appsettings_pasan_validacion_con_credenciales()
    {
        // Server/Database van versionados; solo usuario y clave vienen de secrets.
        var options = new SqlOptions
        {
            Server = "192.168.11.200",
            Database = "SUPPORT_DINAS",
            UserName = "wms_reader",
            Password = "x",
        };

        options.Validate();

        Assert.True(options.Encrypt);
        Assert.True(options.TrustServerCertificate);
        Assert.Equal("dinas-wms-sap-sync", options.ApplicationName);
    }
}
