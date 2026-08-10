using Microsoft.Extensions.Configuration;

namespace DinasWms.SapSync.Persistence;

/// <summary>
/// Fuente de configuración respaldada por SQLite, para los ajustes que se
/// cambian desde la pantalla.
/// </summary>
/// <remarks>
/// Se registra <b>última</b>, así el sistema de configuración de .NET hace la
/// superposición solo: <c>appsettings.json</c> → user-secrets → esto. Nadie
/// reescribe el archivo JSON.
///
/// <para>
/// Ser un <c>IConfigurationSource</c> de verdad —y no un diccionario aparte— es
/// lo que hace que <c>IOptionsMonitor</c> funcione: al guardar se dispara el
/// reload token y los consumidores reciben el valor nuevo sin reiniciar. Si esto
/// fuera un almacén paralelo, el cambio se guardaría bien y no se aplicaría
/// hasta el próximo arranque, que es justo el bug silencioso que hay que evitar.
/// </para>
/// </remarks>
public sealed class SqliteConfigurationSource : IConfigurationSource
{
    private readonly LocalStore _store;

    public SqliteConfigurationSource(LocalStore store) => _store = store;

    /// <summary>
    /// El proveedor vivo. Se guarda para poder pedirle que recargue cuando la
    /// pantalla guarda un valor.
    /// </summary>
    public SqliteConfigurationProvider? Provider { get; private set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder)
    {
        Provider = new SqliteConfigurationProvider(_store);
        return Provider;
    }
}

/// <inheritdoc cref="SqliteConfigurationSource"/>
public sealed class SqliteConfigurationProvider : ConfigurationProvider
{
    private readonly LocalStore _store;

    public SqliteConfigurationProvider(LocalStore store) => _store = store;

    public override void Load() => Data = _store.LeerConfiguracion();

    /// <summary>
    /// Relee de SQLite y <b>avisa a los consumidores</b>.
    /// </summary>
    /// <remarks>
    /// El <c>OnReload()</c> es la línea que importa: es la que dispara el reload
    /// token que <c>IOptionsMonitor</c> escucha. Sin ella el valor quedaría
    /// guardado y el bucle seguiría con el viejo hasta el reinicio.
    /// </remarks>
    public void Recargar()
    {
        Load();
        OnReload();
    }
}
