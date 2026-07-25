using DinasWms.SapSync.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Sql;

/// <summary>Abre conexiones a la Company DB.</summary>
public interface ISapSqlConnectionFactory
{
    /// <summary>
    /// Abre una conexión lista para usar. El llamador es dueño de cerrarla
    /// (<c>await using</c>).
    /// </summary>
    /// <exception cref="SapSqlException">Si no se pudo conectar o autenticar.</exception>
    Task<SqlConnection> OpenAsync(CancellationToken cancellationToken);

    /// <summary>Descripción del destino para logs, sin credenciales.</summary>
    string Target { get; }
}

/// <inheritdoc cref="ISapSqlConnectionFactory"/>
public sealed class SapSqlConnectionFactory : ISapSqlConnectionFactory
{
    private readonly SqlOptions _options;
    private readonly ILogger<SapSqlConnectionFactory> _logger;

    public SapSqlConnectionFactory(IOptions<SqlOptions> options, ILogger<SapSqlConnectionFactory> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public string Target => $"{_options.Server}/{_options.Database} (usuario {_options.UserName})";

    public async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        _options.Validate();

        var connection = new SqlConnection(_options.BuildConnectionString());

        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("Conexión SQL abierta contra {Target}.", Target);
            return connection;
        }
        catch (SqlException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);

            // El mensaje de SQL Server va completo: si es un problema de login o
            // de acceso a la base, ese texto es la instrucción de qué arreglar.
            throw new SapSqlException(
                $"No se pudo conectar a SQL Server en {Target}. " +
                $"Error {ex.Number}: {ex.Message}",
                ex);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
