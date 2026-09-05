using System.Net;

namespace Snail.Toolkit.Nomad;

/// <summary>Nomad answered with an error status; the message carries the body Nomad returned (plain text).</summary>
/// <remarks>
/// The body arrives already cut by the transport, so a misdirected address answering with megabytes
/// cannot reach memory through this message.
/// </remarks>
public sealed class NomadException(
    HttpStatusCode statusCode,
    string message,
    HttpMethod? method = null,
    Uri? requestUri = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>HTTP status of the failed call.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>Method of the failed call, when known.</summary>
    public HttpMethod? Method { get; } = method;

    /// <summary>Address of the failed call, when known.</summary>
    public Uri? RequestUri { get; } = requestUri;
}
