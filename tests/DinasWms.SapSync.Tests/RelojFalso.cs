namespace DinasWms.SapSync.Tests;

/// <summary>
/// Reloj que se mueve a mano. Sin esto, probar que una sesión vence exigiría
/// esperar ocho horas — o sea, no se probaría.
/// </summary>
public sealed class RelojFalso : TimeProvider
{
    private DateTimeOffset _ahora;

    public RelojFalso(DateTimeOffset inicio) => _ahora = inicio;

    public override DateTimeOffset GetUtcNow() => _ahora;

    public void Avanzar(TimeSpan cuanto) => _ahora += cuanto;
}
