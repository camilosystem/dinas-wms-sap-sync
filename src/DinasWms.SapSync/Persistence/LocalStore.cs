using Microsoft.Data.Sqlite;

namespace DinasWms.SapSync.Persistence;

/// <summary>
/// Archivo SQLite local: overrides de configuración e historial de ciclos.
/// </summary>
/// <remarks>
/// Un archivo al lado del ejecutable, sin instalar ni administrar nada. Guarda
/// dos cosas y ninguna más:
///
/// <list type="bullet">
/// <item><b>Overrides de configuración</b> hechos desde la pantalla. Ojo con la
/// jerarquía: <c>appsettings.json</c> sigue siendo la base y esto se superpone
/// encima. La pantalla NUNCA reescribe el archivo — perdería los comentarios,
/// ensuciaría el repo y competiría con quien lo edite a mano. Borrar una fila
/// acá hace que vuelva a mandar el archivo, que es una vuelta atrás limpia.</item>
/// <item><b>Historial de ciclos</b>, porque hoy cada ciclo deja su línea en el
/// log y se olvida, y sin historial no hay forma de ver tendencias.</item>
/// </list>
/// </remarks>
public sealed class LocalStore
{
    private readonly string _cadenaConexion;

    public LocalStore(string rutaArchivo)
    {
        RutaArchivo = rutaArchivo;
        _cadenaConexion = new SqliteConnectionStringBuilder
        {
            DataSource = rutaArchivo,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString();

        Inicializar();
    }

    public string RutaArchivo { get; }

    private void Inicializar()
    {
        using var conexion = Abrir();
        using var cmd = conexion.CreateCommand();

        cmd.CommandText =
            """
            CREATE TABLE IF NOT EXISTS configuracion (
                clave TEXT PRIMARY KEY,
                valor TEXT NOT NULL,
                guardado_en TEXT NOT NULL,
                guardado_por TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS ciclos (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                cuando TEXT NOT NULL,
                disparo TEXT NOT NULL,
                exito INTEGER NOT NULL,
                duracion_ms INTEGER NOT NULL,
                integrados INTEGER NOT NULL,
                fallidos INTEGER NOT NULL,
                error TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_ciclos_cuando ON ciclos (cuando DESC);
            """;

        cmd.ExecuteNonQuery();
    }

    private SqliteConnection Abrir()
    {
        var conexion = new SqliteConnection(_cadenaConexion);
        conexion.Open();
        return conexion;
    }

    // --- Configuración -------------------------------------------------------

    public Dictionary<string, string?> LeerConfiguracion()
    {
        var valores = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        using var conexion = Abrir();
        using var cmd = conexion.CreateCommand();
        cmd.CommandText = "SELECT clave, valor FROM configuracion";

        using var lector = cmd.ExecuteReader();
        while (lector.Read())
        {
            valores[lector.GetString(0)] = lector.GetString(1);
        }

        return valores;
    }

    public void GuardarConfiguracion(string clave, string valor, string usuario)
    {
        using var conexion = Abrir();
        using var cmd = conexion.CreateCommand();

        cmd.CommandText =
            """
            INSERT INTO configuracion (clave, valor, guardado_en, guardado_por)
            VALUES ($clave, $valor, $cuando, $usuario)
            ON CONFLICT(clave) DO UPDATE SET
                valor = $valor, guardado_en = $cuando, guardado_por = $usuario
            """;

        cmd.Parameters.AddWithValue("$clave", clave);
        cmd.Parameters.AddWithValue("$valor", valor);
        cmd.Parameters.AddWithValue("$cuando", DateTimeOffset.Now.ToString("O"));
        cmd.Parameters.AddWithValue("$usuario", usuario);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Borra el override para que vuelva a mandar <c>appsettings.json</c>.</summary>
    public void BorrarConfiguracion(string clave)
    {
        using var conexion = Abrir();
        using var cmd = conexion.CreateCommand();
        cmd.CommandText = "DELETE FROM configuracion WHERE clave = $clave";
        cmd.Parameters.AddWithValue("$clave", clave);
        cmd.ExecuteNonQuery();
    }

    // --- Historial -----------------------------------------------------------

    public void RegistrarCiclo(
        DateTimeOffset cuando,
        string disparo,
        bool exito,
        TimeSpan duracion,
        int integrados,
        int fallidos,
        string? error)
    {
        using var conexion = Abrir();
        using var cmd = conexion.CreateCommand();

        cmd.CommandText =
            """
            INSERT INTO ciclos (cuando, disparo, exito, duracion_ms, integrados, fallidos, error)
            VALUES ($cuando, $disparo, $exito, $duracion, $integrados, $fallidos, $error)
            """;

        cmd.Parameters.AddWithValue("$cuando", cuando.ToString("O"));
        cmd.Parameters.AddWithValue("$disparo", disparo);
        cmd.Parameters.AddWithValue("$exito", exito ? 1 : 0);
        cmd.Parameters.AddWithValue("$duracion", (long)duracion.TotalMilliseconds);
        cmd.Parameters.AddWithValue("$integrados", integrados);
        cmd.Parameters.AddWithValue("$fallidos", fallidos);
        cmd.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<CicloHistorico> LeerHistorial(int max = 100)
    {
        var ciclos = new List<CicloHistorico>();

        using var conexion = Abrir();
        using var cmd = conexion.CreateCommand();
        cmd.CommandText =
            """
            SELECT cuando, disparo, exito, duracion_ms, integrados, fallidos, error
            FROM ciclos ORDER BY id DESC LIMIT $max
            """;
        cmd.Parameters.AddWithValue("$max", max);

        using var lector = cmd.ExecuteReader();
        while (lector.Read())
        {
            ciclos.Add(new CicloHistorico(
                DateTimeOffset.Parse(lector.GetString(0)),
                lector.GetString(1),
                lector.GetInt32(2) == 1,
                lector.GetInt64(3),
                lector.GetInt32(4),
                lector.GetInt32(5),
                lector.IsDBNull(6) ? null : lector.GetString(6)));
        }

        return ciclos;
    }
}

/// <summary>Un ciclo del historial.</summary>
public sealed record CicloHistorico(
    DateTimeOffset Cuando,
    string Disparo,
    bool Exito,
    long DuracionMs,
    int Integrados,
    int Fallidos,
    string? Error);
