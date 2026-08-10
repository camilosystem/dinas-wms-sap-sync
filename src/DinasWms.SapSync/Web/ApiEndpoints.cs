using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Observability;
using DinasWms.SapSync.Persistence;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Web;

/// <summary>
/// Endpoints JSON de la interfaz de monitoreo.
/// </summary>
/// <remarks>
/// Dos reglas que no se negocian acá:
///
/// <list type="number">
/// <item><b>Todo lo que no sea el login exige sesión con rol ADMIN.</b> No hay
/// endpoint alcanzable sin ese chequeo, ni siquiera de solo lectura: el log y el
/// estado dicen qué factura se creó para qué cliente.</item>
/// <item><b>Lo que escribe en SAP es POST y pide confirmación explícita.</b>
/// Nunca GET. Una acción que crea una factura real no puede dispararse por un
/// prefetch del navegador, un crawler, un link compartido o una navegación
/// accidental — y ninguna de esas cosas manda un POST con un cuerpo que diga
/// <c>confirmar: true</c>.</item>
/// </list>
/// </remarks>
public static class ApiEndpoints
{
    private const string EncabezadoToken = "Authorization";

    public static void MapApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // --- Login: la única puerta sin sesión previa -------------------------
        api.MapPost("/login", async (
            SolicitudLogin solicitud,
            ProxyLoginClient proxy,
            WebSessions sesiones,
            CancellationToken ct) =>
        {
            var resultado = await proxy.ValidarAsync(solicitud.Usuario, solicitud.Clave, ct);

            if (!resultado.Ok)
            {
                return Results.Json(new { error = resultado.Error }, statusCode: 401);
            }

            var token = sesiones.Crear(solicitud.Usuario, resultado.Rol!);
            return Results.Json(new { token, usuario = solicitud.Usuario, nombre = resultado.Nombre });
        });

        api.MapPost("/logout", (HttpContext ctx, WebSessions sesiones) =>
        {
            sesiones.Cerrar(LeerToken(ctx));
            return Results.Ok();
        });

        // --- Estado en vivo ---------------------------------------------------
        api.MapGet("/estado", (HttpContext ctx, WebSessions sesiones, SyncStatus estado,
            SyncCycleGate porton, ManualTriggerService disparos) =>
        {
            if (Rechazar(ctx, sesiones) is { } no)
            {
                return no;
            }

            var ocupante = porton.Ocupante;

            return Results.Json(new
            {
                modo = estado.Modo,
                iniciadoEn = estado.IniciadoEn,
                cadenciaSegundos = estado.CadenciaSegundos,
                pasosAutomaticos = estado.PasosAutomaticos,
                sondeos = estado.Sondeos,
                ciclos = estado.Ciclos,
                documentosIntegrados = estado.DocumentosIntegrados,
                documentosFallidos = estado.DocumentosFallidos,
                fallosConsecutivos = estado.FallosConsecutivos,
                sesionSapAbierta = estado.SesionSapAbierta,
                ultimoSondeo = estado.UltimoSondeo,
                ultimoCiclo = estado.UltimoCiclo,
                proximoIntento = estado.ProximoIntento,
                ultimoResultado = estado.UltimoResultado,
                cicloEnCurso = ocupante is null
                    ? null
                    : new { titular = ocupante.Value.Titular, desde = ocupante.Value.Desde },
                ultimoDisparoManual = disparos.Ultimo,
                tiposDisparables = ManualTriggerService.TiposDisponibles.Keys,
            });
        });

        // --- Log incremental --------------------------------------------------
        api.MapGet("/log", (HttpContext ctx, WebSessions sesiones, LogBuffer buffer,
            long desde = 0, int max = 300) =>
        {
            if (Rechazar(ctx, sesiones) is { } no)
            {
                return no;
            }

            var snapshot = buffer.Snapshot(desde, max);

            return Results.Json(new
            {
                lineas = snapshot.Entries.Select(e => new
                {
                    id = e.Id,
                    hora = e.Timestamp.ToString("HH:mm:ss"),
                    nivel = e.Level.ToString(),
                    origen = e.Category,
                    mensaje = e.Message,
                    excepcion = e.Exception,
                }),
                ultimoId = snapshot.LastId,
                descartadas = snapshot.Dropped,
                capacidad = buffer.Capacity,
            });
        });

        // --- Disparo manual: POST, con confirmación ---------------------------
        api.MapPost("/disparar", (HttpContext ctx, WebSessions sesiones,
            SolicitudDisparo solicitud, ManualTriggerService disparos, CancellationToken ct) =>
        {
            if (Rechazar(ctx, sesiones) is { } no)
            {
                return no;
            }

            if (!solicitud.Confirmar)
            {
                // La confirmación no es decorativa: es la diferencia entre una
                // petición intencional y cualquier cosa que llegue por accidente.
                return Results.Json(
                    new { error = "Falta la confirmación explícita: esta acción escribe en SAP." },
                    statusCode: 400);
            }

            if (!ManualTriggerService.TiposDisponibles.ContainsKey(solicitud.Tipo ?? ""))
            {
                return Results.Json(
                    new
                    {
                        error = $"Tipo desconocido: '{solicitud.Tipo}'.",
                        disponibles = ManualTriggerService.TiposDisponibles.Keys,
                    },
                    statusCode: 400);
            }

            var usuario = sesiones.Validar(LeerToken(ctx))!.Usuario;
            var disparo = disparos.Disparar(solicitud.Tipo!, usuario, ct);

            if (disparo is null)
            {
                return Results.Json(
                    new { error = "Ya hay un ciclo en curso. Se rechaza en vez de encolar." },
                    statusCode: 409);
            }

            // 202: aceptado y corriendo en segundo plano. El avance se sigue por
            // /api/estado.
            return Results.Json(disparo, statusCode: 202);
        });

        // --- Configuración ----------------------------------------------------
        api.MapGet("/config", (HttpContext ctx, WebSessions sesiones,
            IOptionsMonitor<ContinuousOptions> opciones, LocalStore almacen) =>
        {
            if (Rechazar(ctx, sesiones) is { } no)
            {
                return no;
            }

            var vigente = opciones.CurrentValue;

            return Results.Json(new
            {
                pollSeconds = vigente.PollSeconds,
                maxBackoffSeconds = vigente.MaxBackoffSeconds,
                failuresBeforeBackoff = vigente.FailuresBeforeBackoff,
                limites = new
                {
                    pollMinimo = ContinuousOptions.PollSecondsMinimo,
                    pollMaximo = ContinuousOptions.PollSecondsMaximo,
                    backoffMaximo = ContinuousOptions.MaxBackoffSegundosMaximo,
                },
                // Qué está sobreescrito desde la pantalla y qué viene del
                // archivo: sin esto nadie sabría por qué un valor no es el que
                // dice appsettings.json.
                overrides = almacen.LeerConfiguracion(),
            });
        });

        api.MapPost("/config", (HttpContext ctx, WebSessions sesiones, SolicitudConfig solicitud,
            IOptionsMonitor<ContinuousOptions> opciones, LocalStore almacen,
            SqliteConfigurationSource fuente, ILoggerFactory logs) =>
        {
            if (Rechazar(ctx, sesiones) is { } no)
            {
                return no;
            }

            var vigente = opciones.CurrentValue;

            // Se arma el candidato y se valida ENTERO antes de tocar nada. La
            // validación va en el borde a propósito: el bucle nunca debe llegar
            // a ver una configuración inválida.
            var candidato = new ContinuousOptions
            {
                PollSeconds = solicitud.PollSeconds ?? vigente.PollSeconds,
                MaxBackoffSeconds = solicitud.MaxBackoffSeconds ?? vigente.MaxBackoffSeconds,
                FailuresBeforeBackoff =
                    solicitud.FailuresBeforeBackoff ?? vigente.FailuresBeforeBackoff,
                RunOnStartup = vigente.RunOnStartup,
            };

            try
            {
                candidato.Validate();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Json(new { error = ex.Message }, statusCode: 400);
            }

            var usuario = sesiones.Validar(LeerToken(ctx))!.Usuario;
            var seccion = ContinuousOptions.SectionName;

            almacen.GuardarConfiguracion(
                $"{seccion}:{nameof(ContinuousOptions.PollSeconds)}",
                candidato.PollSeconds.ToString(), usuario);
            almacen.GuardarConfiguracion(
                $"{seccion}:{nameof(ContinuousOptions.MaxBackoffSeconds)}",
                candidato.MaxBackoffSeconds.ToString(), usuario);
            almacen.GuardarConfiguracion(
                $"{seccion}:{nameof(ContinuousOptions.FailuresBeforeBackoff)}",
                candidato.FailuresBeforeBackoff.ToString(), usuario);

            // Acá es donde el cambio se vuelve efectivo sin reiniciar: relee de
            // SQLite y dispara el reload token que escucha IOptionsMonitor.
            fuente.Provider?.Recargar();

            logs.CreateLogger("Configuracion").LogWarning(
                "{Usuario} cambió la cadencia: sondeo {Poll}s, back-off hasta {Max}s tras {Umbral} " +
                "fallos. Se aplica sin reiniciar.",
                usuario,
                candidato.PollSeconds,
                candidato.MaxBackoffSeconds,
                candidato.FailuresBeforeBackoff);

            return Results.Json(new
            {
                pollSeconds = opciones.CurrentValue.PollSeconds,
                maxBackoffSeconds = opciones.CurrentValue.MaxBackoffSeconds,
                failuresBeforeBackoff = opciones.CurrentValue.FailuresBeforeBackoff,
            });
        });

        // --- Historial --------------------------------------------------------
        api.MapGet("/historial", (HttpContext ctx, WebSessions sesiones, LocalStore almacen,
            int max = 50) =>
        {
            if (Rechazar(ctx, sesiones) is { } no)
            {
                return no;
            }

            return Results.Json(almacen.LeerHistorial(Math.Clamp(max, 1, 500)));
        });
    }

    /// <summary>
    /// Devuelve un 401 si no hay sesión válida con rol ADMIN, o null si puede pasar.
    /// </summary>
    private static IResult? Rechazar(HttpContext ctx, WebSessions sesiones)
    {
        var sesion = sesiones.Validar(LeerToken(ctx));

        if (sesion is null)
        {
            return Results.Json(new { error = "Sesión inválida o vencida." }, statusCode: 401);
        }

        if (!string.Equals(sesion.Rol, ProxyLoginClient.RolRequerido, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Json(new { error = "Se requiere rol ADMIN." }, statusCode: 403);
        }

        return null;
    }

    private static string? LeerToken(HttpContext ctx)
    {
        var encabezado = ctx.Request.Headers[EncabezadoToken].ToString();

        return encabezado.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? encabezado["Bearer ".Length..].Trim()
            : null;
    }
}

/// <summary>Cuerpo de <c>POST /api/login</c>.</summary>
public sealed record SolicitudLogin(string Usuario, string Clave);

/// <summary>Cuerpo de <c>POST /api/disparar</c>.</summary>
public sealed record SolicitudDisparo(string? Tipo, bool Confirmar);

/// <summary>
/// Cuerpo de <c>POST /api/config</c>. Todo opcional: lo que no venga conserva
/// su valor vigente, así cambiar una cosa no obliga a reenviar el resto.
/// </summary>
public sealed record SolicitudConfig(
    int? PollSeconds,
    int? MaxBackoffSeconds,
    int? FailuresBeforeBackoff);
