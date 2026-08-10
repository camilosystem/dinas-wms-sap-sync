using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Observability;

/// <summary>Una línea de log guardada para que la pantalla pueda leerla.</summary>
/// <param name="Id">
/// Correlativo monotónico. Es lo que permite que la pantalla pida "dame lo que
/// haya después del id N" y reciba solo lo nuevo, en vez de retransmitir el
/// buffer entero cada pocos segundos.
/// </param>
public sealed record LogEntry(
    long Id,
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? Exception);

/// <summary>Lo que devuelve una consulta al buffer.</summary>
/// <param name="Entries">Las líneas nuevas, en orden.</param>
/// <param name="LastId">
/// El id más alto que existe ahora. La pantalla lo manda en la próxima consulta.
/// </param>
/// <param name="Dropped">
/// Cuántas líneas se perdieron por desborde desde que arrancó el proceso. Que
/// esto crezca significa que el buffer es chico para el ritmo de log, y es mejor
/// saberlo que descubrir un hueco silencioso en la pantalla.
/// </param>
public sealed record LogSnapshot(IReadOnlyList<LogEntry> Entries, long LastId, long Dropped);

/// <summary>
/// Anillo en memoria con las últimas N líneas de log.
/// </summary>
/// <remarks>
/// El worker escribe desde su bucle y la web lee desde requests HTTP, así que
/// esto es lo único del proyecto con concurrencia real de verdad. Se resuelve
/// con un <c>lock</c> corto sobre un array de tamaño fijo:
///
/// <list type="bullet">
/// <item>No se usa una cola concurrente porque hace falta descartar por
/// antigüedad con tope duro, y eso obliga a coordinar de todas formas.</item>
/// <item>El snapshot devuelve una COPIA. Si devolviera el array vivo, la web
/// podría estar recorriéndolo mientras el worker lo pisa.</item>
/// <item>La contención es irrelevante al ritmo real: el worker escribe unas
/// pocas líneas por minuto y la pantalla lee una vez cada pocos segundos.</item>
/// </list>
/// </remarks>
public sealed class LogBuffer
{
    private readonly object _candado = new();
    private readonly LogEntry?[] _anillo;

    private long _proximoId = 1;
    private long _descartadas;
    private int _escrituras;

    public LogBuffer(int capacity = 2000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(capacity), capacity, "El buffer necesita al menos una posición.");
        }

        _anillo = new LogEntry?[capacity];
    }

    public int Capacity => _anillo.Length;

    public void Add(LogLevel level, string category, string message, string? exception)
    {
        lock (_candado)
        {
            var posicion = (int)(_escrituras % _anillo.Length);

            // Si esa posición ya tenía algo, se está pisando una línea que nadie
            // llegó a leer. Se cuenta para poder decirlo.
            if (_anillo[posicion] is not null)
            {
                _descartadas++;
            }

            _anillo[posicion] = new LogEntry(
                _proximoId++, DateTimeOffset.Now, level, category, message, exception);

            _escrituras++;
        }
    }

    /// <summary>
    /// Devuelve las líneas con id mayor que <paramref name="sinceId"/>.
    /// </summary>
    /// <param name="max">
    /// Tope de líneas por respuesta. Evita que una pantalla que estuvo cerrada
    /// una hora se traiga todo el anillo de una.
    /// </param>
    public LogSnapshot Snapshot(long sinceId = 0, int max = 500)
    {
        if (max <= 0)
        {
            max = 1;
        }

        lock (_candado)
        {
            var nuevas = new List<LogEntry>(Math.Min(max, _anillo.Length));

            // Se recorre en orden de escritura, no de posición: el anillo tiene
            // el corte en cualquier lado.
            var desde = Math.Max(0, _escrituras - _anillo.Length);

            for (var i = desde; i < _escrituras && nuevas.Count < max; i++)
            {
                var entrada = _anillo[(int)(i % _anillo.Length)];

                if (entrada is not null && entrada.Id > sinceId)
                {
                    nuevas.Add(entrada);
                }
            }

            return new LogSnapshot(nuevas, _proximoId - 1, _descartadas);
        }
    }
}
