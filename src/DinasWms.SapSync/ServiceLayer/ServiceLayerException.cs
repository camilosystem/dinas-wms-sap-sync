using System.Net;

namespace DinasWms.SapSync.ServiceLayer;

/// <summary>
/// Error devuelto por Service Layer, o error al hablar con él. Lleva el cuerpo
/// crudo de la respuesta: en esta fase de ensayo y error el mensaje exacto de
/// SAP es la información más útil que hay.
/// </summary>
public sealed class ServiceLayerException : Exception
{
    public ServiceLayerException(
        string message,
        HttpStatusCode? statusCode = null,
        string? responseBody = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode? StatusCode { get; }

    public string? ResponseBody { get; }
}
