using Microsoft.Extensions.Logging;

namespace Snail.Toolkit.Nomad;

/// <summary>What the client records about the changes it makes to a cluster.</summary>
/// <remarks>
/// Reads are left to the request logging <see cref="IHttpClientFactory"/> already provides. Only the
/// three calls that change a cluster are recorded here, because an operator asking "who deployed
/// this and when" cannot answer it from a request log that says nothing about the evaluation.
/// </remarks>
internal static partial class NomadLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Registered Nomad job {JobId} as evaluation {EvalId}.")]
    public static partial void JobRegistered(ILogger logger, string jobId, string evalId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Scaled task group {TaskGroup} of Nomad job {JobId} to {Count} as evaluation {EvalId}.")]
    public static partial void JobScaled(ILogger logger, string jobId, string taskGroup, int count, string evalId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Information, Message = "Stopped Nomad job {JobId} (purge {Purge}) as evaluation {EvalId}.")]
    public static partial void JobStopped(ILogger logger, string jobId, bool purge, string evalId);
}
