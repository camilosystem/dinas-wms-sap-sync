using System.Net;
using System.Text.Json;
using DinasWms.SapSync.Configuration;
using DinasWms.SapSync.Middleware;
using DinasWms.SapSync.Observability;
using DinasWms.SapSync.Sql;
using DinasWms.SapSync.Sync;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// Omisión de tareas de pago por configuración: sacar del ciclo de reintentos una
/// tarea que no se puede integrar todavía, sin destruir su evidencia.
/// </summary>
/// <remarks>
/// El caso que la motivó es la tarea 49: un pago con CHEQUE que Service Layer
/// rechaza porque el payload todavía no lleva todos los campos que exige. Es un
/// bloque de trabajo aplazado. Mientras tanto la tarea reintentaba cada 300
/// segundos para siempre, y un monitor en rojo permanente hace que un fallo real
/// no se distinga del ruido.
///
/// <para>
/// La tarea omitida NO se reporta al middleware: sigue PENDIENTE en la cola, con
/// su payload y su <c>error_detail</c> intactos. Ese es el punto — son la
/// evidencia del bloque aplazado cuando se retome.
/// </para>
///
/// <para>
/// Las dos propiedades que estos tests fijan, y que no se pueden comprobar con un
/// solo caso en la cola:
/// </para>
/// <list type="number">
/// <item>La exclusión es del <c>task_id</c> EXACTO y de ninguno más. Una
/// exclusión demasiado ancha se ve idéntica a una correcta mientras haya una
/// sola tarea.</item>
/// <item>Con la lista vacía la tarea vuelve al ciclo. Si no volviera, no sería
/// una exclusión configurable sino un bloqueo disfrazado.</item>
/// </list>
/// </remarks>
public class PaymentTaskOmissionTests
{
    private static SapAccountPaymentSyncTask Tarea(int id) =>
        new() { TaskId = id, DocumentUuid = $"uuid-{id}", Status = "PENDIENTE" };

    // ---------------------------------------------------------------- reparto

    [Fact]
    public void ExcluyeElIdExacto_yNingunOtro()
    {
        var cola = new[] { Tarea(48), Tarea(49), Tarea(50) };

        var (aProcesar, omitidas) = IncomingPaymentsSyncStep.RepartirPorOmision(cola, [49]);

        // Lo que importa: 48 y 50 SIGUEN. Con una cola de una sola tarea, una
        // exclusión que se llevara todo daría el mismo resultado que esta.
        Assert.Equal([48, 50], aProcesar.Select(t => t.TaskId));
        Assert.Equal([49], omitidas);
    }

    [Fact]
    public void ConLaListaVacia_laTareaVuelveAlCiclo()
    {
        var cola = new[] { Tarea(49) };

        var (aProcesar, omitidas) = IncomingPaymentsSyncStep.RepartirPorOmision(cola, []);

        Assert.Equal([49], aProcesar.Select(t => t.TaskId));
        Assert.Empty(omitidas);
    }

    [Fact]
    public void SinConfiguracion_noSeOmiteNada()
    {
        var cola = new[] { Tarea(49), Tarea(50) };

        var (aProcesar, omitidas) = IncomingPaymentsSyncStep.RepartirPorOmision(cola, null);

        Assert.Equal([49, 50], aProcesar.Select(t => t.TaskId));
        Assert.Empty(omitidas);
    }

    [Fact]
    public void UnIdConfiguradoQueNoEstaEnLaCola_noEstorba()
    {
        var cola = new[] { Tarea(50) };

        var (aProcesar, omitidas) = IncomingPaymentsSyncStep.RepartirPorOmision(cola, [49]);

        Assert.Equal([50], aProcesar.Select(t => t.TaskId));
        Assert.Empty(omitidas);
    }

    // ------------------------------------------------- cableado de punta a punta

    [Fact]
    public async Task SoloLaOmitidaEnLaCola_elWorkerVeQueNoHayTrabajo()
    {
        // Es la propiedad que apaga el rojo: si HasPendingWork dice que no hay
        // trabajo, el bucle no corre ciclo, no abre sesión con SAP, y pone su
        // contador de fallos consecutivos en cero.
        var (paso, estado) = Armar([Tarea(49)], omitidos: [49]);

        Assert.False(await paso.HasPendingWorkAsync(default));
        Assert.Equal([49], estado.OmitidasEnLaCola);
        Assert.Equal([49], estado.OmitidasConfiguradas);
    }

    [Fact]
    public async Task ConOtraTareaAlLado_siHayTrabajo()
    {
        // El mismo escenario que arriba pero con una segunda tarea de pago: la
        // omisión no puede apagar el paso entero.
        var (paso, estado) = Armar([Tarea(49), Tarea(50)], omitidos: [49]);

        Assert.True(await paso.HasPendingWorkAsync(default));
        Assert.Equal([49], estado.OmitidasEnLaCola);
    }

    [Fact]
    public async Task ConLaListaVacia_la49VuelveAContarComoTrabajo()
    {
        var (paso, estado) = Armar([Tarea(49)], omitidos: []);

        Assert.True(await paso.HasPendingWorkAsync(default));
        Assert.Empty(estado.OmitidasEnLaCola);
        Assert.Empty(estado.OmitidasConfiguradas);
    }

    [Fact]
    public async Task LaOmisionSePublicaAunqueLaColaEsteVacia()
    {
        // La decisión vigente tiene que verse SIEMPRE, no solo cuando se ejerce.
        // Si solo apareciera al encontrarla en la cola, el verde volvería a
        // significar "todo bien salvo lo que decidimos no mirar".
        var (paso, estado) = Armar([], omitidos: [49]);

        Assert.False(await paso.HasPendingWorkAsync(default));
        Assert.Empty(estado.OmitidasEnLaCola);
        Assert.Equal([49], estado.OmitidasConfiguradas);
    }

    // ------------------------------------------------------------------ arnés

    private static (IncomingPaymentsSyncStep Paso, SyncStatus Estado) Armar(
        SapAccountPaymentSyncTask[] cola,
        int[] omitidos)
    {
        var estado = new SyncStatus(TimeProvider.System);

        var paso = new IncomingPaymentsSyncStep(
            new MiddlewareFalso(cola),
            new ResolverQueNoSeUsa(),
            Options.Create(new PaymentsOptions { TaskIdsOmitidos = omitidos }),
            Options.Create(new MiddlewareOptions { MaxTasksPerCycle = 50 }),
            NullLogger<IncomingPaymentsSyncStep>.Instance,
            estado);

        return (paso, estado);
    }

    /// <summary>Devuelve la cola pedida. Cualquier POST haría fallar el test.</summary>
    private sealed class MiddlewareFalso(SapAccountPaymentSyncTask[] cola) : IMiddlewareClient
    {
        public string BaseUrl => "http://falso/";

        public DateTimeOffset? TokenExpiresAtUtc => null;

        public Task LoginAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<(HttpStatusCode StatusCode, string Body)> GetAsync(
            string relativePath,
            CancellationToken cancellationToken)
        {
            var json = JsonSerializer.Serialize(new SapAccountPaymentSyncTasksPage { Tasks = [.. cola] });
            return Task.FromResult((HttpStatusCode.OK, json));
        }

        // Una tarea omitida NO se reporta: sigue PENDIENTE con su evidencia. Si
        // alguna vez se reportara, este test truena en vez de pasar.
        public Task<(HttpStatusCode StatusCode, string Body)> PostJsonAsync(
            string relativePath,
            string json,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"No se debe reportar nada al middleware en este escenario. Ruta: {relativePath}");
    }

    private sealed class ResolverQueNoSeUsa : IDocEntryResolver
    {
        public Task<InvoiceLookupResult> LookupInvoiceAsync(
            string cardCode,
            int docNum,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("No se resuelve ningún DocEntry en estos tests.");
    }
}
