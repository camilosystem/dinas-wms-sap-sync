namespace DinasWms.SapSync.ServiceLayer;

/// <summary>
/// Abre una sesión de Service Layer para UN ciclo de trabajo.
/// </summary>
/// <remarks>
/// Patrón de sesión decidido para este proyecto: <b>login/logout por ciclo</b>,
/// no sesión persistente con renovación. Cada ciclo (ventana programada o
/// "forzar ahora") abre su propia sesión, trabaja, y la cierra. No hay tracking
/// de expiración ni renovación proactiva — la única concesión es un reintento
/// único ante un 401 inesperado a mitad de ciclo (ver
/// <see cref="ServiceLayerSession.SendAsync"/>).
/// </remarks>
public interface IServiceLayerSessionFactory
{
    /// <summary>
    /// Hace <c>POST /Login</c> y devuelve la sesión ya autenticada. El llamador
    /// es dueño del objeto: al hacer <c>DisposeAsync</c> se ejecuta el
    /// <c>Logout</c>, liberando el slot de sesión del lado de SAP.
    /// </summary>
    /// <exception cref="ServiceLayerException">Si el login falla.</exception>
    Task<ServiceLayerSession> OpenAsync(CancellationToken cancellationToken);
}
