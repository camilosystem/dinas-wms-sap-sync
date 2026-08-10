using Microsoft.Extensions.Logging;

namespace DinasWms.SapSync.Observability;

/// <summary>
/// Provider de logging que además de la consola deja las líneas en el
/// <see cref="LogBuffer"/>, para que la pantalla pueda mostrarlas.
/// </summary>
/// <remarks>
/// Hasta ahora el log iba solo a consola y se perdía al cerrar la ventana: para
/// medir cualquier cosa de esta semana hubo que redirigir a archivos a mano.
///
/// Tiene su propio nivel mínimo, independiente del de consola, para poder subir
/// a Debug desde la pantalla sin ensuciar la consola ni reiniciar el proceso.
/// </remarks>
[ProviderAlias("Buffer")]
public sealed class LogBufferProvider : ILoggerProvider
{
    private readonly LogBuffer _buffer;

    public LogBufferProvider(LogBuffer buffer) => _buffer = buffer;

    /// <summary>
    /// Nivel mínimo que entra al buffer. Mutable a propósito: cambiarlo es una
    /// perilla de la pantalla, no una decisión de arranque.
    /// </summary>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    public ILogger CreateLogger(string categoryName) => new BufferLogger(this, categoryName);

    public void Dispose()
    {
        // El buffer vive en memoria y lo administra el contenedor; no hay nada
        // que soltar acá.
    }

    private sealed class BufferLogger : ILogger
    {
        private readonly LogBufferProvider _provider;
        private readonly string _categoria;

        public BufferLogger(LogBufferProvider provider, string categoria)
        {
            _provider = provider;
            _categoria = categoria;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel != LogLevel.None && logLevel >= _provider.MinimumLevel;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            // La categoría se acorta al último segmento: en la pantalla,
            // "ContinuousSyncWorker" dice lo mismo que el namespace completo y
            // deja lugar para el mensaje.
            var corta = _categoria.LastIndexOf('.') is var punto && punto >= 0
                ? _categoria[(punto + 1)..]
                : _categoria;

            _provider._buffer.Add(
                logLevel,
                corta,
                formatter(state, exception),
                exception?.ToString());
        }
    }
}
