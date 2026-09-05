using System.Net;

namespace Snail.Toolkit.Nomad;

/// <summary>Nomad answered with an error status; the message carries the body Nomad returned (plain text).</summary>
public sealed class NomadException(
    HttpStatusCode statusCode,
    string message,
    HttpMethod? method = null,
    Uri? requestUri = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    /// <summary>Longest error body kept; a longer one is cut and marked with an ellipsis.</summary>
    /// <remarks>
    /// A misdirected address can answer with megabytes, and the status is the news either way.
    /// </remarks>
    public const int BodyLimit = 4096;

    /// <summary>HTTP status of the failed call.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>Method of the failed call, when known.</summary>
    public HttpMethod? Method { get; } = method;

    /// <summary>Address of the failed call, when known.</summary>
    public Uri? RequestUri { get; } = requestUri;
}
