using System.Net;

namespace Snail.Toolkit.Nomad;

/// <summary>Nomad answered with an error status; the message carries the body Nomad returned (plain text).</summary>
public sealed class NomadException(HttpStatusCode statusCode, string message) : Exception(message)
{
    /// <summary>HTTP status of the failed call.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
}
