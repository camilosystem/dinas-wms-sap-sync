using DinasWms.SapSync.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DinasWms.SapSync.Sql;

/// <summary>
/// Resuelve el <c>DocEntry</c> de un documento a partir de los datos que sí
/// viajan por el contrato del middleware.
/// </summary>
/// <remarks>
/// Esta es la pieza que sostiene una regla de arquitectura del proyecto: el
/// <c>DocEntry</c> nunca viaja por el middleware ni por las apps — el resto del
/// ecosistema solo conoce <c>DocNum</c>/<c>invoice_doc_num</c>. Antes de armar
/// cualquier payload de Service Layer que necesite <c>DocEntry</c>, se resuelve
/// acá con una consulta local.
///
/// Solo lectura: este módulo nunca escribe en SQL.
/// </remarks>
public interface IDocEntryResolver
{
    /// <summary>
    /// Devuelve el <c>DocEntry</c> de la factura de venta (<c>OINV</c>)
    /// identificada por cliente y número de documento, o <c>null</c> si no
    /// existe.
    /// </summary>
    /// <exception cref="AmbiguousInvoiceException">
    /// Si hay más de una factura con ese <c>CardCode</c> + <c>DocNum</c>.
    /// </exception>
    /// <exception cref="SapSqlException">Si falla la conexión o la consulta.</exception>
    Task<int?> ResolveInvoiceDocEntryAsync(
        string cardCode,
        int docNum,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDocEntryResolver"/>
public sealed class DocEntryResolver : IDocEntryResolver
{
    // Se seleccionan todas las coincidencias (no TOP 1 ni ExecuteScalar) a
    // propósito: hay que poder detectar el caso ambiguo en vez de tomar
    // silenciosamente la primera fila. Series viaja solo para poder reportar la
    // ambigüedad de forma útil.
    private const string QueryFacturaPorDocNum = """
        SELECT DocEntry, Series
        FROM OINV
        WHERE DocNum = @docNum AND CardCode = @cardCode
        """;

    private readonly ISapSqlConnectionFactory _connectionFactory;
    private readonly SqlOptions _options;
    private readonly ILogger<DocEntryResolver> _logger;

    public DocEntryResolver(
        ISapSqlConnectionFactory connectionFactory,
        IOptions<SqlOptions> options,
        ILogger<DocEntryResolver> logger)
    {
        _connectionFactory = connectionFactory;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int?> ResolveInvoiceDocEntryAsync(
        string cardCode,
        int docNum,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cardCode);

        if (docNum <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(docNum), docNum, "DocNum debe ser un entero positivo.");
        }

        await using var connection = await _connectionFactory
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        var coincidencias = new List<(int DocEntry, int Series)>();

        try
        {
            await using var command = new SqlCommand(QueryFacturaPorDocNum, connection)
            {
                CommandTimeout = _options.CommandTimeoutSeconds,
            };

            // Parametrizado: CardCode viene de datos externos (el middleware), así
            // que nunca se interpola en el SQL.
            command.Parameters.Add("@docNum", System.Data.SqlDbType.Int).Value = docNum;
            command.Parameters.Add("@cardCode", System.Data.SqlDbType.NVarChar, 50).Value = cardCode;

            await using var reader = await command
                .ExecuteReaderAsync(cancellationToken)
                .ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                coincidencias.Add((reader.GetInt32(0), reader.GetInt32(1)));
            }
        }
        catch (SqlException ex)
        {
            throw new SapSqlException(
                $"Falló la consulta de DocEntry en OINV (CardCode='{cardCode}', DocNum={docNum}) " +
                $"contra {_connectionFactory.Target}. Error {ex.Number}: {ex.Message}",
                ex);
        }

        if (coincidencias.Count == 0)
        {
            _logger.LogWarning(
                "No existe factura en OINV con CardCode='{CardCode}' y DocNum={DocNum}.",
                cardCode,
                docNum);
            return null;
        }

        if (coincidencias.Count > 1)
        {
            throw new AmbiguousInvoiceException(cardCode, docNum, coincidencias);
        }

        var (docEntry, series) = coincidencias[0];
        _logger.LogInformation(
            "DocEntry resuelto: CardCode='{CardCode}', DocNum={DocNum} → DocEntry={DocEntry} (Series={Series}).",
            cardCode,
            docNum,
            docEntry,
            series);

        return docEntry;
    }
}
