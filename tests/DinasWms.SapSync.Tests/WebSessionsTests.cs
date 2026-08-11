using DinasWms.SapSync.Web;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// Esta es la única barrera entre cualquiera que alcance el puerto 5280 y los
/// botones que crean facturas reales en SAP.
/// </summary>
/// <remarks>
/// Las pruebas están escritas para <b>fallar si la seguridad se rompe</b>, no
/// para pasar cuando funciona. Un test de camino feliz —"con un token válido
/// entra"— seguiría en verde con la expiración desactivada o el chequeo de rol
/// borrado, que es justo lo que no queremos.
/// </remarks>
public class WebSessionsTests
{
    private static readonly DateTimeOffset Inicio = new(2026, 8, 11, 10, 0, 0, TimeSpan.Zero);

    private static (WebSessions Sesiones, RelojFalso Reloj) Crear(TimeSpan? duracion = null)
    {
        var reloj = new RelojFalso(Inicio);
        var sesiones = new WebSessions(reloj);

        if (duracion is not null)
        {
            sesiones.Duracion = duracion.Value;
        }

        return (sesiones, reloj);
    }

    // --- Expiración ---------------------------------------------------------

    [Fact]
    public void JustoAntesDeVencer_todaviaVale()
    {
        var (sesiones, reloj) = Crear(TimeSpan.FromHours(8));
        var token = sesiones.Crear("admin1", "ADMIN");

        reloj.Avanzar(TimeSpan.FromHours(8) - TimeSpan.FromSeconds(1));

        Assert.NotNull(sesiones.Validar(token));
    }

    [Fact]
    public void AlVencer_dejaDeValer()
    {
        // Si alguien quitara el chequeo de expiración, este test se pone rojo.
        var (sesiones, reloj) = Crear(TimeSpan.FromHours(8));
        var token = sesiones.Crear("admin1", "ADMIN");

        reloj.Avanzar(TimeSpan.FromHours(8));

        Assert.Null(sesiones.Validar(token));
    }

    [Fact]
    public void MuchoDespuesDeVencer_sigueSinValer()
    {
        var (sesiones, reloj) = Crear(TimeSpan.FromHours(8));
        var token = sesiones.Crear("admin1", "ADMIN");

        reloj.Avanzar(TimeSpan.FromDays(30));

        Assert.Null(sesiones.Validar(token));
    }

    [Fact]
    public void UnaSesionVencida_seDescartaYNoOcupaLugar()
    {
        var (sesiones, reloj) = Crear(TimeSpan.FromHours(1));
        sesiones.Crear("admin1", "ADMIN");

        Assert.Equal(1, sesiones.Activas);

        reloj.Avanzar(TimeSpan.FromHours(2));

        Assert.Equal(0, sesiones.Activas);
    }

    // --- Tokens -------------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("token-inventado")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void UnTokenQueNoEmitimos_noVale(string? token)
    {
        var (sesiones, _) = Crear();
        sesiones.Crear("admin1", "ADMIN");

        Assert.Null(sesiones.Validar(token));
    }

    [Fact]
    public void CadaSesionRecibeUnTokenDistinto()
    {
        var (sesiones, _) = Crear();

        var tokens = Enumerable.Range(0, 200).Select(_ => sesiones.Crear("admin1", "ADMIN")).ToList();

        Assert.Equal(200, tokens.Distinct().Count());
    }

    [Fact]
    public void ElTokenTieneEntropiaSuficiente()
    {
        // 32 bytes en base64. Si alguien lo acortara a algo adivinable, esto
        // salta.
        var (sesiones, _) = Crear();

        var token = sesiones.Crear("admin1", "ADMIN");

        Assert.True(Convert.FromBase64String(token).Length >= 32);
    }

    [Fact]
    public void AlCerrar_elTokenDejaDeValerDeInmediato()
    {
        var (sesiones, _) = Crear();
        var token = sesiones.Crear("admin1", "ADMIN");

        sesiones.Cerrar(token);

        Assert.Null(sesiones.Validar(token));
    }

    [Fact]
    public void CerrarUnaSesion_noAfectaALasDemas()
    {
        var (sesiones, _) = Crear();
        var uno = sesiones.Crear("admin1", "ADMIN");
        var dos = sesiones.Crear("otro", "ADMIN");

        sesiones.Cerrar(uno);

        Assert.Null(sesiones.Validar(uno));
        Assert.NotNull(sesiones.Validar(dos));
    }

    // --- Autorización: sesión + rol -----------------------------------------

    [Fact]
    public void ConRolAdmin_autoriza()
    {
        var (sesiones, _) = Crear();
        var token = sesiones.Crear("admin1", "ADMIN");

        var (decision, sesion) = sesiones.Autorizar(token, "ADMIN");

        Assert.Equal(Autorizacion.Ok, decision);
        Assert.Equal("admin1", sesion!.Usuario);
    }

    [Theory]
    [InlineData("VENDEDOR")]
    [InlineData("BODEGA")]
    [InlineData("DRIVER")]
    [InlineData("")]
    [InlineData("admin")]  // ojo: minúsculas SÍ deben pasar; ver el test de abajo
    public void ConOtroRol_noAutoriza(string rol)
    {
        // Excepto "admin", que es el mismo rol con otra caja: se separa aparte.
        var (sesiones, _) = Crear();
        var token = sesiones.Crear("usuario", rol);

        var (decision, _) = sesiones.Autorizar(token, "ADMIN");

        if (string.Equals(rol, "ADMIN", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(Autorizacion.Ok, decision);
        }
        else
        {
            Assert.Equal(Autorizacion.RolInsuficiente, decision);
        }
    }

    [Fact]
    public void UnRolInsuficiente_seDistingueDeNoTenerSesion()
    {
        // La diferencia importa: 403 dice "sos vos pero no podés", 401 dice
        // "volvé a entrar". Confundirlos manda a alguien a reintentar el login
        // para siempre.
        var (sesiones, _) = Crear();
        var token = sesiones.Crear("vendedor1", "VENDEDOR");

        var (conSesion, _) = sesiones.Autorizar(token, "ADMIN");
        var (sinSesion, _) = sesiones.Autorizar("token-que-no-existe", "ADMIN");

        Assert.Equal(Autorizacion.RolInsuficiente, conSesion);
        Assert.Equal(Autorizacion.SinSesion, sinSesion);
    }

    [Fact]
    public void UnaSesionAdminVencida_noAutoriza()
    {
        // El caso combinado: rol correcto pero sesión muerta. Si la expiración
        // se rompiera, este pasaría igual que el de rol — por eso va aparte.
        var (sesiones, reloj) = Crear(TimeSpan.FromHours(8));
        var token = sesiones.Crear("admin1", "ADMIN");

        reloj.Avanzar(TimeSpan.FromHours(9));

        var (decision, sesion) = sesiones.Autorizar(token, "ADMIN");

        Assert.Equal(Autorizacion.SinSesion, decision);
        Assert.Null(sesion);
    }

    // --- Concurrencia -------------------------------------------------------

    [Fact]
    public async Task ConCreacionesYValidacionesEnParalelo_nadaSePierdeNiSeMezcla()
    {
        var (sesiones, _) = Crear();
        var largada = new TaskCompletionSource();
        var emitidos = new System.Collections.Concurrent.ConcurrentBag<string>();

        var tareas = Enumerable.Range(0, 30).Select(async i =>
        {
            await largada.Task;
            for (var j = 0; j < 20; j++)
            {
                var token = sesiones.Crear($"usuario{i}", "ADMIN");
                emitidos.Add(token);
                Assert.Equal($"usuario{i}", sesiones.Validar(token)!.Usuario);
            }
        }).ToArray();

        largada.SetResult();
        await Task.WhenAll(tareas);

        Assert.Equal(600, emitidos.Distinct().Count());
        Assert.Equal(600, sesiones.Activas);
    }
}
