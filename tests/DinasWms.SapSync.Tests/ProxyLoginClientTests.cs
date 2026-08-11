using System.Net;
using System.Text;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// El proxy del login es lo que decide quién entra. Se prueba contra un
/// middleware simulado para poder ejercitar los casos que un servidor real no
/// deja provocar a voluntad: credenciales malas, rol insuficiente, caída de red
/// y respuestas rotas.
/// </summary>
public class ProxyLoginClientTests
{
    /// <summary>Handler que devuelve lo que se le diga, y recuerda qué le pidieron.</summary>
    private sealed class HandlerFalso : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public HandlerFalso(Func<HttpRequestMessage, HttpResponseMessage> responder) =>
            _responder = responder;

        public string? UltimoCuerpo { get; private set; }
        public Uri? UltimaUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            UltimaUri = request.RequestUri;
            UltimoCuerpo = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return _responder(request);
        }
    }

    private static HttpResponseMessage Respuesta(HttpStatusCode codigo, string cuerpo) =>
        new(codigo) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };

    private static (ProxyLoginClient Cliente, HandlerFalso Handler) Crear(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new HandlerFalso(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://middleware-de-prueba/v1/") };

        var opciones = Options.Create(new MiddlewareOptions
        {
            BaseUrl = "http://middleware-de-prueba/v1/",
            LoginPath = "auth/login",
            UserName = "x",
            Password = "y",
        });

        return (new ProxyLoginClient(http, opciones, NullLogger<ProxyLoginClient>.Instance), handler);
    }

    // --- El chequeo de rol, que es lo que protege los botones ---------------

    [Fact]
    public async Task ConRolAdmin_dejaEntrar()
    {
        var (cliente, _) = Crear(_ => Respuesta(
            HttpStatusCode.OK,
            """{"token":"jwt-del-middleware","role":"ADMIN","display_name":"Camila Torres"}"""));

        var r = await cliente.ValidarAsync("admin1", "clave", default);

        Assert.True(r.Ok);
        Assert.Equal("ADMIN", r.Rol);
        Assert.Equal("Camila Torres", r.Nombre);
    }

    [Theory]
    [InlineData("VENDEDOR")]
    [InlineData("BODEGA")]
    [InlineData("DRIVER")]
    [InlineData("SUPERVISOR")]
    public async Task ConCredencialesValidasPeroOtroRol_NO_dejaEntrar(string rol)
    {
        // El caso peligroso: el middleware dice 200 y devuelve un token bueno.
        // Si el chequeo de rol se borrara, esto pasaría y un vendedor podría
        // apretar los botones que crean facturas.
        var (cliente, _) = Crear(_ => Respuesta(
            HttpStatusCode.OK,
            $$"""{"token":"jwt-valido","role":"{{rol}}","display_name":"Alguien"}"""));

        var r = await cliente.ValidarAsync("usuario", "clave", default);

        Assert.False(r.Ok);
        Assert.Contains("ADMIN", r.Error);
    }

    [Fact]
    public async Task SinRolEnLaRespuesta_NO_dejaEntrar()
    {
        // Ausencia de rol no se interpreta como permiso.
        var (cliente, _) = Crear(_ => Respuesta(
            HttpStatusCode.OK, """{"token":"jwt-valido","display_name":"Alguien"}"""));

        var r = await cliente.ValidarAsync("usuario", "clave", default);

        Assert.False(r.Ok);
    }

    [Fact]
    public async Task ElRolSeComparaSinDistinguirMayusculas()
    {
        var (cliente, _) = Crear(_ => Respuesta(
            HttpStatusCode.OK, """{"token":"t","role":"admin","display_name":"x"}"""));

        Assert.True((await cliente.ValidarAsync("admin1", "clave", default)).Ok);
    }

    // --- Credenciales y fallos ----------------------------------------------

    [Fact]
    public async Task ConCredencialesMalas_noDejaEntrar()
    {
        var (cliente, _) = Crear(_ => Respuesta(HttpStatusCode.Unauthorized, """{"error":"nope"}"""));

        var r = await cliente.ValidarAsync("admin1", "mala", default);

        Assert.False(r.Ok);
        Assert.Contains("incorrect", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, "clave")]
    [InlineData("", "clave")]
    [InlineData("admin1", null)]
    [InlineData("admin1", "")]
    [InlineData("   ", "   ")]
    public async Task SinUsuarioOSinClave_niSiquieraPregunta(string? usuario, string? clave)
    {
        var pidio = false;
        var (cliente, _) = Crear(_ =>
        {
            pidio = true;
            return Respuesta(HttpStatusCode.OK, """{"token":"t","role":"ADMIN"}""");
        });

        var r = await cliente.ValidarAsync(usuario!, clave!, default);

        Assert.False(r.Ok);
        Assert.False(pidio);
    }

    [Fact]
    public async Task SiElMiddlewareNoResponde_esUnErrorDistintoDeCredencialesMalas()
    {
        // "No pude preguntar" y "la respuesta es no" son cosas distintas.
        // Confundirlas haría que una caída del middleware parezca una contraseña
        // equivocada, y alguien perdería media hora reescribiéndola.
        var (cliente, _) = Crear(_ => throw new HttpRequestException("sin ruta al host"));

        var r = await cliente.ValidarAsync("admin1", "clave", default);

        Assert.False(r.Ok);
        Assert.Contains("contactar", r.Error, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("incorrect", r.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SiElMiddlewareDevuelveBasura_noDejaEntrar()
    {
        var (cliente, _) = Crear(_ => Respuesta(HttpStatusCode.OK, "esto no es json"));

        var r = await cliente.ValidarAsync("admin1", "clave", default);

        Assert.False(r.Ok);
    }

    [Fact]
    public async Task AnteUn500delMiddleware_noDejaEntrar()
    {
        var (cliente, _) = Crear(_ => Respuesta(HttpStatusCode.InternalServerError, "{}"));

        var r = await cliente.ValidarAsync("admin1", "clave", default);

        Assert.False(r.Ok);
    }

    // --- Qué se le manda al middleware --------------------------------------

    [Fact]
    public async Task LasCredencialesVanEnElCuerpoYAlEndpointDeLogin()
    {
        var (cliente, handler) = Crear(_ => Respuesta(
            HttpStatusCode.OK, """{"token":"t","role":"ADMIN","display_name":"x"}"""));

        await cliente.ValidarAsync("admin1", "secreta", default);

        Assert.EndsWith("auth/login", handler.UltimaUri!.AbsolutePath);
        Assert.Contains("\"username\":\"admin1\"", handler.UltimoCuerpo);
        Assert.Contains("\"password\":\"secreta\"", handler.UltimoCuerpo);
    }
}
