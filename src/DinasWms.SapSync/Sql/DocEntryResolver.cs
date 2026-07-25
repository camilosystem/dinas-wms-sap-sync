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
/// Solo lectura, y solo sobre vistas <c>vw_WMS_*</c>: las tablas de SAP están
/// cerradas para <c>wms_reader</c> a propósito, y eso no se rodea.
/// </remarks>
public interface IDocEntryResolver
{
    /// <summary>
    /// Busca la factura de venta identificada por cliente y número de documento.
    /// </summary>
    /// <remarks>
    /// Nunca devuelve <c>null</c>: el resultado distingue explícitamente entre
    /// no encontrada, cerrada y anulada, porque cada caso pide una reacción
    /// distinta del sincronizador.
    /// </remarks>
    /// <exception cref="AmbiguousInvoiceException">
    /// Si hay más de una factura con ese cliente y número.
    /// </exception>
    /// <exception cref="SapSqlException">Si falla la conexión o la consulta.</exception>
    Task<InvoiceLookupResult> LookupInvoiceAsync(
        string cardCode,
        int docNum,
        CancellationToken cancellationToken);
}

/// <inheritdoc cref="IDocEntryResolver"/>
public sealed class DocEntryResolver : IDocEntryResolver
{
    /// <summary>
    /// Vista de lectura. Expone TODAS las facturas sin filtrar por estado, que es
    /// justo lo que permite distinguir "no existe" de "cerrada" de "anulada".
    /// </summary>
    public const string ViewName = "dbo.vw_WMS_InvoiceLookup";

    // No se usa TOP 1 ni ExecuteScalar: hay que poder detectar el caso ambiguo en
    // vez de tomar silenciosamente la primera fila. La vista es solo de facturas
    // (OINV), así que no hace falta filtrar por tipo de documento.
    private const string QueryFacturaPorDocNum = $"""
        SELECT doc_entry, series, doc_status, is_canceled, doc_total, paid_amount, open_amount
        FROM {ViewName}
        WHERE doc_num = @docNum AND client_code = @cardCode
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

    public async Task<InvoiceLookupResult> LookupInvoiceAsync(
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

        var filas = await LeerFilasAsync(cardCode, docNum, cancellationToken).ConfigureAwait(false);

        // --- Caso 1: no existe ------------------------------------------------
        if (filas.Count == 0)
        {
            _logger.LogError(
                "ERROR DE DATOS — no existe factura con client_code='{CardCode}' y doc_num={DocNum} " +
                "en {Vista}. El middleware envió una referencia que SAP no reconoce.",
                cardCode,
                docNum,
                ViewName);

            return InvoiceLookupResult.NotFound(cardCode, docNum);
        }

        if (filas.Count > 1)
        {
            throw new AmbiguousInvoiceException(
                cardCode, docNum, filas.Select(f => f.DocEntry).ToArray());
        }

        var fila = filas[0];

        // El orden importa: una factura anulada también aparece cerrada, y anulada
        // es el diagnóstico más grave de los dos.
        // --- Caso 2: anulada --------------------------------------------------
        if (fila.IsCanceled)
        {
            _logger.LogError(
                "ERROR DE NEGOCIO — la factura client_code='{CardCode}', doc_num={DocNum} " +
                "(DocEntry={DocEntry}) está ANULADA en SAP. No se aplica el pago.",
                cardCode,
                docNum,
                fila.DocEntry);

            return Construir(InvoiceLookupOutcome.Canceled, cardCode, docNum, fila);
        }

        // --- Caso 3: cerrada --------------------------------------------------
        if (!string.Equals(fila.DocStatus, "O", StringComparison.OrdinalIgnoreCase))
        {
            if (fila.OpenAmount != 0)
            {
                // Cerrada pero con saldo: no se cerró por pago completo (pudo ser
                // una nota de crédito o un cierre manual). No cambia la decisión
                // de descartar, pero no debe pasar inadvertido.
                _logger.LogWarning(
                    "La factura client_code='{CardCode}', doc_num={DocNum} (DocEntry={DocEntry}) " +
                    "está cerrada (DocStatus='{Status}') pero con saldo {Saldo}. No se cerró por " +
                    "pago completo — vale revisar por qué.",
                    cardCode,
                    docNum,
                    fila.DocEntry,
                    fila.DocStatus,
                    fila.OpenAmount);
            }
            else
            {
                _logger.LogInformation(
                    "DUPLICADO BENIGNO — la factura client_code='{CardCode}', doc_num={DocNum} " +
                    "(DocEntry={DocEntry}) ya está cerrada y saldada. Se descarta sin aplicar el pago.",
                    cardCode,
                    docNum,
                    fila.DocEntry);
            }

            return Construir(InvoiceLookupOutcome.Closed, cardCode, docNum, fila);
        }

        // --- Caso 4: abierta, se puede pagar ----------------------------------
        _logger.LogInformation(
            "DocEntry resuelto: client_code='{CardCode}', doc_num={DocNum} → DocEntry={DocEntry} " +
            "(series={Series}, doc_total={DocTotal}, paid={Paid}, open={Open}).",
            cardCode,
            docNum,
            fila.DocEntry,
            fila.Series,
            fila.DocTotal,
            fila.PaidAmount,
            fila.OpenAmount);

        return Construir(InvoiceLookupOutcome.Resolved, cardCode, docNum, fila);
    }

    private async Task<List<Fila>> LeerFilasAsync(
        string cardCode,
        int docNum,
        CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        var filas = new List<Fila>();

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
                filas.Add(new Fila(
                    DocEntry: Convert.ToInt32(reader.GetValue(0)),
                    Series: reader.IsDBNull(1) ? null : Convert.ToInt32(reader.GetValue(1)),
                    DocStatus: reader.IsDBNull(2) ? null : Convert.ToString(reader.GetValue(2))?.Trim(),
                    IsCanceled: !reader.IsDBNull(3) &&
                        string.Equals(
                            Convert.ToString(reader.GetValue(3))?.Trim(),
                            "Y",
                            StringComparison.OrdinalIgnoreCase),
                    DocTotal: Convert.ToDecimal(reader.GetValue(4)),
                    PaidAmount: Convert.ToDecimal(reader.GetValue(5)),
                    OpenAmount: Convert.ToDecimal(reader.GetValue(6))));
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

        return filas;
    }

    private static InvoiceLookupResult Construir(
        InvoiceLookupOutcome outcome,
        string cardCode,
        int docNum,
        Fila fila) =>
        new(outcome,
            cardCode,
            docNum,
            fila.DocEntry,
            fila.Series,
            fila.DocStatus,
            fila.IsCanceled,
            fila.DocTotal,
            fila.PaidAmount,
            fila.OpenAmount);

    private sealed record Fila(
        int DocEntry,
        int? Series,
        string? DocStatus,
        bool IsCanceled,
        decimal DocTotal,
        decimal PaidAmount,
        decimal OpenAmount);
}
