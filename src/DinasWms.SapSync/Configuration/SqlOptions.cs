using Microsoft.Data.SqlClient;

namespace DinasWms.SapSync.Configuration;

/// <summary>
/// Conexión SQL directa a la Company DB, usada solo para resolver datos que no
/// viajan por el contrato del middleware (hoy: <c>DocEntry</c> a partir de
/// <c>DocNum</c>).
/// </summary>
/// <remarks>
/// Se reutiliza el login <c>wms_reader</c>, el mismo que ya usa
/// dinas-wms-middleware — decisión tomada para no multiplicar logins en SQL
/// Server. Es un usuario de lectura, y este módulo solo hace <c>SELECT</c>:
/// nada de este proyecto escribe en SQL, las escrituras van todas por Service
/// Layer.
///
/// Usuario y contraseña vienen de <c>dotnet user-secrets</c>; servidor y base
/// van versionados porque no son secretos.
/// </remarks>
public sealed class SqlOptions
{
    public const string SectionName = "Sql";

    /// <summary>Instancia de SQL Server. IP de LAN del servidor.</summary>
    public string Server { get; set; } = "";

    /// <summary>Company DB a consultar (ej. SUPPORT_DINAS).</summary>
    public string Database { get; set; } = "";

    public string UserName { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>
    /// Cifrado del canal. Microsoft.Data.SqlClient lo exige por defecto desde
    /// la versión 4, así que dejarlo activo es lo correcto.
    /// </summary>
    public bool Encrypt { get; set; } = true;

    /// <summary>
    /// Confiar en el certificado del servidor SQL sin validar la cadena.
    /// </summary>
    /// <remarks>
    /// ⚠ SEGURIDAD: igual que con Service Layer, SQL Server acá usa un
    /// certificado autofirmado. Con <see cref="Encrypt"/> activo, sin esto la
    /// conexión falla por validación de cadena. Es aceptable porque el destino
    /// es un servidor conocido en la LAN, pero es un bypass real de validación:
    /// no reutilizar esta configuración para conectarse a nada fuera de la red
    /// de oficina.
    /// </remarks>
    public bool TrustServerCertificate { get; set; } = true;

    public int ConnectTimeoutSeconds { get; set; } = 15;

    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Nombre de aplicación que se ve en las sesiones de SQL Server. Ayuda a
    /// distinguir este tráfico del middleware cuando se diagnostica en el
    /// servidor con el mismo login.
    /// </summary>
    public string ApplicationName { get; set; } = "dinas-wms-sap-sync";

    /// <summary>
    /// Arma la cadena de conexión.
    /// </summary>
    /// <remarks>
    /// Se usa <see cref="SqlConnectionStringBuilder"/> y no concatenación de
    /// texto: una contraseña con <c>;</c>, <c>=</c> o <c>'</c> rompería una
    /// cadena armada a mano (o peor, la alteraría en silencio). El builder
    /// escapa los valores correctamente.
    /// </remarks>
    public string BuildConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = Server,
            InitialCatalog = Database,
            UserID = UserName,
            Password = Password,
            IntegratedSecurity = false,
            Encrypt = Encrypt,
            TrustServerCertificate = TrustServerCertificate,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = ApplicationName,
            MultipleActiveResultSets = false,
        };

        return builder.ConnectionString;
    }

    public void Validate()
    {
        var faltantes = new List<string>();

        if (string.IsNullOrWhiteSpace(Server)) faltantes.Add($"{SectionName}:{nameof(Server)}");
        if (string.IsNullOrWhiteSpace(Database)) faltantes.Add($"{SectionName}:{nameof(Database)}");
        if (string.IsNullOrWhiteSpace(UserName)) faltantes.Add($"{SectionName}:{nameof(UserName)}");
        if (string.IsNullOrWhiteSpace(Password)) faltantes.Add($"{SectionName}:{nameof(Password)}");

        if (faltantes.Count > 0)
        {
            throw new InvalidOperationException(
                "Configuración de SQL incompleta. Falta: " + string.Join(", ", faltantes) +
                ". Las credenciales se cargan con: dotnet user-secrets set \"Sql:UserName\" \"...\"");
        }

        if (ConnectTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(ConnectTimeoutSeconds)} debe ser mayor que 0.");
        }

        if (CommandTimeoutSeconds <= 0)
        {
            throw new InvalidOperationException(
                $"{SectionName}:{nameof(CommandTimeoutSeconds)} debe ser mayor que 0.");
        }
    }
}
