using Microsoft.Data.SqlClient;

namespace DinasWms.SapSync.Sql;

/// <summary>
/// Error hablando con SQL Server. Conserva el número de error de SQL y el
/// mensaje original sin reformatear: cuando falta un permiso, ese texto exacto
/// es lo que se necesita para hacer el GRANT correcto.
/// </summary>
public class SapSqlException : Exception
{
    public SapSqlException(string message, SqlException? sqlException = null)
        : base(message, sqlException)
    {
        SqlErrorNumber = sqlException?.Number;
        OriginalSqlMessage = sqlException?.Message;
    }

    public int? SqlErrorNumber { get; }

    public string? OriginalSqlMessage { get; }

    /// <summary>
    /// True si el error viene de permisos o de credenciales, no de un problema
    /// de red o de datos. Distingue "hay que hacer un GRANT" de "hay que
    /// revisar la consulta".
    /// </summary>
    public bool IsAccessProblem => SqlErrorNumber is
        18456 or  // Login failed for user
        4060 or   // Cannot open database requested by the login
        916 or    // El principal no puede acceder a la base en este contexto
        262 or    // Permiso denegado en la base de datos
        297 or    // El usuario no tiene permiso para realizar esta acción
        229 or    // SELECT denegado sobre el objeto
        230 or    // SELECT denegado sobre la columna
        208;      // Nombre de objeto no válido (o invisible por permisos)
}

/// <summary>
/// Se encontró más de una factura para el mismo <c>CardCode</c> + <c>DocNum</c>.
/// </summary>
/// <remarks>
/// En SAP B1, <c>OINV.DocNum</c> es único POR SERIE, no globalmente: con varias
/// series de numeración el mismo DocNum puede repetirse. Agregar el CardCode
/// reduce la probabilidad pero no la elimina.
///
/// Por eso esto falla ruidosamente en vez de tomar la primera fila: resolver el
/// DocEntry equivocado significaría aplicar un pago contra la factura
/// equivocada, y eso no se arregla solo.
/// </remarks>
public sealed class AmbiguousInvoiceException : SapSqlException
{
    public AmbiguousInvoiceException(string cardCode, int docNum, IReadOnlyList<int> docEntries)
        : base(
            $"Se encontraron {docEntries.Count} facturas con client_code='{cardCode}' y " +
            $"doc_num={docNum}: DocEntry " + string.Join(", ", docEntries) +
            ". DocNum es único por serie, no globalmente, así que la pareja " +
            "client_code+doc_num no alcanza para identificar el documento. No se elige " +
            "ninguna: hay que decidir cómo desambiguar (probablemente exponer la serie " +
            "en la vista e incluirla en el contrato del middleware).")
    {
        CardCode = cardCode;
        DocNum = docNum;
        DocEntries = docEntries;
    }

    public string CardCode { get; }

    public int DocNum { get; }

    public IReadOnlyList<int> DocEntries { get; }
}
