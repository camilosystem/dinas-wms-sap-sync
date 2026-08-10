using DinasWms.SapSync.Observability;
using Microsoft.Extensions.Logging;
using Xunit;

namespace DinasWms.SapSync.Tests;

/// <summary>
/// El worker escribe desde su bucle y la web lee desde requests: es lo único del
/// proyecto con concurrencia real. Un buffer que se corrompa bajo carga no
/// lanzaría una excepción visible — devolvería líneas repetidas, salteadas o
/// desordenadas, que es un fallo mucho peor porque parece que anda.
/// </summary>
public class LogBufferTests
{
    private static void Escribir(LogBuffer buffer, string mensaje) =>
        buffer.Add(LogLevel.Information, "Prueba", mensaje, null);

    [Fact]
    public void ReciénCreado_noTieneNada()
    {
        var snapshot = new LogBuffer(10).Snapshot();

        Assert.Empty(snapshot.Entries);
        Assert.Equal(0, snapshot.LastId);
        Assert.Equal(0, snapshot.Dropped);
    }

    [Fact]
    public void LosIdsSonCorrelativosYEmpiezanEnUno()
    {
        var buffer = new LogBuffer(10);
        Escribir(buffer, "a");
        Escribir(buffer, "b");

        var snapshot = buffer.Snapshot();

        Assert.Equal([1L, 2L], snapshot.Entries.Select(e => e.Id));
        Assert.Equal(2, snapshot.LastId);
    }

    [Fact]
    public void PidiendoDesdeUnId_soloLleganLasNuevas()
    {
        // Es lo que evita retransmitir el buffer entero cada tres segundos.
        var buffer = new LogBuffer(10);
        Escribir(buffer, "a");
        Escribir(buffer, "b");

        var primera = buffer.Snapshot();
        Escribir(buffer, "c");

        var segunda = buffer.Snapshot(primera.LastId);

        Assert.Single(segunda.Entries);
        Assert.Equal("c", segunda.Entries[0].Message);
    }

    [Fact]
    public void SinNadaNuevo_devuelveVacioYNoRepite()
    {
        var buffer = new LogBuffer(10);
        Escribir(buffer, "a");

        var primera = buffer.Snapshot();
        var segunda = buffer.Snapshot(primera.LastId);

        Assert.Empty(segunda.Entries);
        Assert.Equal(primera.LastId, segunda.LastId);
    }

    [Fact]
    public void AlDesbordar_conservaLasUltimasYCuentaLasPerdidas()
    {
        var buffer = new LogBuffer(3);

        for (var i = 1; i <= 5; i++)
        {
            Escribir(buffer, $"linea {i}");
        }

        var snapshot = buffer.Snapshot();

        Assert.Equal(3, snapshot.Entries.Count);
        Assert.Equal(["linea 3", "linea 4", "linea 5"], snapshot.Entries.Select(e => e.Message));
        Assert.Equal(5, snapshot.LastId);

        // Que esto crezca significa que el buffer es chico para el ritmo de log.
        // Es mejor saberlo que descubrir un hueco silencioso en la pantalla.
        Assert.Equal(2, snapshot.Dropped);
    }

    [Fact]
    public void ElTopePorRespuesta_seRespeta()
    {
        // Una pantalla que estuvo cerrada una hora no debe traerse todo de una.
        var buffer = new LogBuffer(100);

        for (var i = 0; i < 50; i++)
        {
            Escribir(buffer, $"linea {i}");
        }

        var snapshot = buffer.Snapshot(max: 10);

        Assert.Equal(10, snapshot.Entries.Count);
        Assert.Equal(50, snapshot.LastId);
    }

    [Fact]
    public void ElSnapshotEsUnaCopia_yNoSeVeAfectadoPorEscriturasPosteriores()
    {
        var buffer = new LogBuffer(10);
        Escribir(buffer, "a");

        var snapshot = buffer.Snapshot();
        Escribir(buffer, "b");

        Assert.Single(snapshot.Entries);
    }

    [Fact]
    public async Task ConEscriturasYLecturasEnParalelo_nadaSeRompeNiSeRepite()
    {
        // Ocho escritores y cuatro lectores a la vez, que es más presión de la
        // que va a tener nunca. Lo que se afirma es lo importante: los ids que
        // ve un lector nunca se repiten ni retroceden.
        var buffer = new LogBuffer(500);
        var largada = new TaskCompletionSource();
        const int porEscritor = 250;

        var escritores = Enumerable.Range(0, 8).Select(async e =>
        {
            await largada.Task;
            for (var i = 0; i < porEscritor; i++)
            {
                Escribir(buffer, $"escritor {e} linea {i}");
            }
        }).ToArray();

        var idsVistosPorLector = new List<List<long>>();
        var candado = new object();

        var lectores = Enumerable.Range(0, 4).Select(async _ =>
        {
            await largada.Task;
            var vistos = new List<long>();
            long desde = 0;

            for (var i = 0; i < 100; i++)
            {
                var snapshot = buffer.Snapshot(desde, max: 50);
                foreach (var entrada in snapshot.Entries)
                {
                    vistos.Add(entrada.Id);
                }

                if (snapshot.Entries.Count > 0)
                {
                    desde = snapshot.Entries[^1].Id;
                }

                await Task.Yield();
            }

            lock (candado)
            {
                idsVistosPorLector.Add(vistos);
            }
        }).ToArray();

        largada.SetResult();
        await Task.WhenAll(escritores.Concat(lectores));

        var final = buffer.Snapshot();
        Assert.Equal(8 * porEscritor, final.LastId);

        foreach (var vistos in idsVistosPorLector)
        {
            Assert.Equal(vistos.Count, vistos.Distinct().Count());
            Assert.Equal(vistos.OrderBy(x => x), vistos);
        }
    }

    [Fact]
    public void CapacidadNoPositiva_noSeAdmite()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LogBuffer(0));
    }
}
