using DinasWms.SapSync.Observability;
using DinasWms.SapSync.Sync;
using DinasWms.SapSync.Web;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// El disparo manual es la puerta que escribe en SAP desde la pantalla. Lo que
/// se prueba acá es que no se saltee el portón, que registre quién apretó, y que
/// un ciclo que revienta no la deje colgada en "EN_CURSO" para siempre.
/// </summary>
public class ManualTriggerServiceTests
{
    private sealed class CicloEspia : ISyncCycle
    {
        private readonly Func<SyncCycleTrigger, IReadOnlyCollection<string>?, SyncCycleResult> _responder;
        private readonly TaskCompletionSource? _bloqueo;

        public CicloEspia(
            Func<SyncCycleTrigger, IReadOnlyCollection<string>?, SyncCycleResult> responder,
            TaskCompletionSource? bloqueo = null)
        {
            _responder = responder;
            _bloqueo = bloqueo;
        }

        public IReadOnlyCollection<string>? PasosPedidos { get; private set; }
        public int Ejecuciones { get; private set; }

        public async Task<SyncCycleResult> RunAsync(
            SyncCycleTrigger trigger,
            CancellationToken cancellationToken,
            IReadOnlyCollection<string>? soloPasos = null)
        {
            PasosPedidos = soloPasos;
            Ejecuciones++;

            if (_bloqueo is not null)
            {
                await _bloqueo.Task;
            }

            return _responder(trigger, soloPasos);
        }
    }

    private static SyncCycleResult Ok() =>
        new(SyncCycleTrigger.Forced, true, TimeSpan.FromSeconds(1), 3, 0, ["paso: 3 procesados"]);

    private static (ManualTriggerService Servicio, SyncCycleGate Porton, CicloEspia Ciclo) Crear(
        ISyncCycle? ciclo = null)
    {
        var porton = new SyncCycleGate(NullLogger<SyncCycleGate>.Instance);
        var espia = ciclo as CicloEspia ?? new CicloEspia((_, _) => Ok());

        var servicio = new ManualTriggerService(
            espia,
            porton,
            new SyncStatus(TimeProvider.System),
            TimeProvider.System,
            NullLogger<ManualTriggerService>.Instance);

        return (servicio, porton, espia);
    }

    private static async Task EsperarQueTermine(ManualTriggerService servicio)
    {
        for (var i = 0; i < 200 && servicio.Ultimo?.Estado == "EN_CURSO"; i++)
        {
            await Task.Delay(20);
        }
    }

    // --- El portón ----------------------------------------------------------

    [Fact]
    public async Task SiYaHayUnCicloEnCurso_noDispara()
    {
        // Es el 409 de la API. Si esto devolviera un disparo igual, se abrirían
        // dos sesiones contra SAP a la vez.
        var (servicio, porton, ciclo) = Crear();

        using var enCurso = await porton.TryEnterAsync("bucle continuo");

        var disparo = servicio.Disparar("facturas", "admin1", default);

        Assert.Null(disparo);
        Assert.Equal(0, ciclo.Ejecuciones);
    }

    [Fact]
    public async Task AlLiberarseElPorton_yaSePuedeDisparar()
    {
        var (servicio, porton, _) = Crear();

        var permiso = await porton.TryEnterAsync("bucle continuo");
        permiso!.Dispose();

        Assert.NotNull(servicio.Disparar("facturas", "admin1", default));
        await EsperarQueTermine(servicio);
    }

    // --- Qué se dispara -----------------------------------------------------

    [Theory]
    [InlineData("facturas", "OrderInvoices")]
    [InlineData("pagos", "IncomingPayments")]
    [InlineData("notas-credito", "CreditNotes")]
    public async Task CadaTipoCorreSuPasoYNingunOtro(string tipo, string pasoEsperado)
    {
        var (servicio, _, ciclo) = Crear();

        servicio.Disparar(tipo, "admin1", default);
        await EsperarQueTermine(servicio);

        Assert.Equal([pasoEsperado], ciclo.PasosPedidos);
    }

    [Theory]
    [InlineData("cualquiera")]
    [InlineData("")]
    [InlineData("ORDERINVOICES")]
    public void UnTipoDesconocido_noDisparaNada(string tipo)
    {
        var (servicio, _, ciclo) = Crear();

        Assert.Throws<ArgumentException>(() => servicio.Disparar(tipo, "admin1", default));
        Assert.Equal(0, ciclo.Ejecuciones);
    }

    [Fact]
    public async Task ElTipoNoDistingueMayusculas()
    {
        var (servicio, _, ciclo) = Crear();

        servicio.Disparar("FACTURAS", "admin1", default);
        await EsperarQueTermine(servicio);

        Assert.Equal(["OrderInvoices"], ciclo.PasosPedidos);
    }

    // --- Quién apretó -------------------------------------------------------

    [Fact]
    public async Task QuedaRegistradoQuienDisparo()
    {
        // Los documentos que salen de acá son reales. Saber quién apretó no es
        // decorativo.
        var (servicio, _, _) = Crear();

        servicio.Disparar("pagos", "admin1", default);
        await EsperarQueTermine(servicio);

        Assert.Equal("admin1", servicio.Ultimo!.Usuario);
        Assert.Equal("pagos", servicio.Ultimo.Tipo);
    }

    // --- Que no quede colgado -----------------------------------------------

    [Fact]
    public async Task SiElCicloRevienta_terminaEnERRORyNoEnEN_CURSO()
    {
        // Sin el try/catch de la tarea de fondo, la excepción moriría en un Task
        // huérfano y la pantalla mostraría EN_CURSO para siempre.
        var ciclo = new CicloEspia((_, _) => throw new InvalidOperationException("revienta"));
        var (servicio, porton, _) = Crear(ciclo);

        servicio.Disparar("facturas", "admin1", default);
        await EsperarQueTermine(servicio);

        Assert.Equal("ERROR", servicio.Ultimo!.Estado);
        Assert.Contains("revienta", servicio.Ultimo.Detalle);
        Assert.NotNull(servicio.Ultimo.Terminado);
        Assert.False(porton.EnUso);
    }

    [Fact]
    public async Task UnCicloRechazadoPorElPorton_seReportaComoRECHAZADO()
    {
        var ciclo = new CicloEspia((t, _) => SyncCycleResult.Rejected(t, "ya hay un ciclo"));
        var (servicio, _, _) = Crear(ciclo);

        servicio.Disparar("facturas", "admin1", default);
        await EsperarQueTermine(servicio);

        Assert.Equal("RECHAZADO", servicio.Ultimo!.Estado);
    }

    [Fact]
    public async Task UnCicloConFallos_noSeReportaComoOK()
    {
        var ciclo = new CicloEspia((t, _) =>
            new SyncCycleResult(t, false, TimeSpan.Zero, 1, 2, [], "dos fallaron"));
        var (servicio, _, _) = Crear(ciclo);

        servicio.Disparar("facturas", "admin1", default);
        await EsperarQueTermine(servicio);

        Assert.Equal("CON_FALLOS", servicio.Ultimo!.Estado);
        Assert.Equal(1, servicio.Ultimo.Integrados);
        Assert.Equal(2, servicio.Ultimo.Fallidos);
    }

    [Fact]
    public void MientrasCorre_seReportaEN_CURSOyNoBloqueaAlLlamador()
    {
        // El endpoint devuelve 202 y sigue: un ciclo con la cola cargada puede
        // tardar minutos y un request colgado sería peor diagnóstico.
        var bloqueo = new TaskCompletionSource();
        var ciclo = new CicloEspia((_, _) => Ok(), bloqueo);
        var (servicio, _, _) = Crear(ciclo);

        var disparo = servicio.Disparar("facturas", "admin1", default);

        Assert.Equal("EN_CURSO", disparo!.Estado);
        Assert.Null(disparo.Terminado);

        bloqueo.SetResult();
    }
}
