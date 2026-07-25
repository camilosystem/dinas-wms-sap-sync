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
    /// <summary>
    /// Vista de lectura. NO se consulta <c>OINV</c> directamente: el principio del
    /// proyecto es que las vistas <c>vw_WMS_*</c> son la única superficie de
    /// lectura sobre SAP, y <c>wms_reader</c> tiene permisos solo sobre ellas.
    /// </summary>
    public const string ViewName = "dbo.vw_WMS_ClientDocuments";

    // Notas sobre esta consulta:
    //
    //  · doc_type = 'INVOICE' es obligatorio, no cosmético: la vista unifica OINV
    //    y ORIN con UNION ALL, y cada tabla tiene su propia secuencia de DocNum.
    //    Sin el filtro, un doc_num que exista como factura Y como nota de crédito
    //    del mismo cliente devolvería dos filas.
    //
    //  · No se usa TOP 1 ni ExecuteScalar: hay que poder detectar el caso ambiguo
    //    en vez de tomar silenciosamente la primera fila.
    //
    //  · doc_total y open_amount viajan solo para el log — dan contexto al
    //    diagnosticar. La lógica de montos de IncomingPayments es fase posterior.
    private const string QueryFacturaPorDocNum = $"""
        SELECT doc_entry, doc_total, open_amount, days_overdue
        FROM {ViewName}
        WHERE doc_type = 'INVOICE'
          AND doc_num = @docNum
          AND client_code = @cardCode
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

        var coincidencias = new List<Coincidencia>();

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
                // Convert.* en vez de GetDecimal/GetInt32 directos: los tipos
                // numéricos de SAP varían por columna y no se asumen.
                coincidencias.Add(new Coincidencia(
                    Convert.ToInt32(reader.GetValue(0)),
                    Convert.ToDecimal(reader.GetValue(1)),
                    Convert.ToDecimal(reader.GetValue(2)),
                    reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3))));
            }
        }
        catch (SqlException ex)
        {
            throw new SapSqlException(
                $"Falló la consulta de DocEntry en {ViewName} (client_code='{cardCode}', " +
                $"doc_num={docNum}) contra {_connectionFactory.Target}. " +
                $"Error {ex.Number}: {ex.Message}",
                ex);
        }

        if (coincidencias.Count == 0)
        {
            // Ojo con interpretar esto: la vista solo expone documentos ABIERTOS
            // (DocStatus = 'O'). "No encontrado" cubre tres casos distintos que
            // desde acá no se distinguen: la factura no existe, ya está pagada
            // por completo, o está cancelada.
            _logger.LogWarning(
                "No se encontró factura ABIERTA con client_code='{CardCode}' y doc_num={DocNum} " +
                "en {Vista}. Puede que no exista, que ya esté pagada, o que esté cancelada — " +
                "la vista solo expone documentos abiertos.",
                cardCode,
                docNum,
                ViewName);
            return null;
        }

        if (coincidencias.Count > 1)
        {
            throw new AmbiguousInvoiceException(
                cardCode, docNum, coincidencias.Select(c => c.DocEntry).ToArray());
        }

        var match = coincidencias[0];
        _logger.LogInformation(
            "DocEntry resuelto: client_code='{CardCode}', doc_num={DocNum} → DocEntry={DocEntry} " +
            "(doc_total={DocTotal}, open_amount={OpenAmount}, days_overdue={DaysOverdue}).",
            cardCode,
            docNum,
            match.DocEntry,
            match.DocTotal,
            match.OpenAmount,
            match.DaysOverdue);

        return match.DocEntry;
    }

    private sealed record Coincidencia(
        int DocEntry,
        decimal DocTotal,
        decimal OpenAmount,
        int? DaysOverdue);
}
