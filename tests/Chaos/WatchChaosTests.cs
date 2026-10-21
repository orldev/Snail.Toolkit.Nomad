using System.Net;

namespace Snail.Toolkit.Nomad.Tests.Chaos;

/// <summary>
/// A watch is the one long-lived call of the client, so what it does at the edges of its settings and of its token
/// is what an operator's dashboard does there.
/// </summary>
public sealed class WatchChaosTests
{
    private const string Job = """
        {"ID":"web","Name":"web","Namespace":"default","Type":"service","Status":"running",
         "StatusDescription":"","Version":0,"Stop":false,"SubmitTime":1,"TaskGroups":[{"Name":"server","Count":1}]}
        """;

    /// <summary>
    /// A WatchWait of 500 ms passes validation and reaches Nomad as milliseconds; truncated to whole seconds it was
    /// sent as wait=0s, which Nomad reads as its five-minute default, and the client's Timeout aborted every request.
    /// </summary>
    [Fact]
    public async Task WatchJob_WithAWatchWaitUnderASecond_AsksNomadForThatWait()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, Job, ("X-Nomad-Index", "7"));
        var client = RecordingHandler.Client(handler, options => options.WatchWait = TimeSpan.FromMilliseconds(500));

        await foreach (var _ in client.WatchJobAsync("web"))
        {
            break;
        }

        Assert.Contains("wait=500ms", handler.Requests[0].RequestUri!.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// A token cancelled before the first request ends the watch by throwing; ending it quietly, the way a purged
    /// job does, left a dashboard shutting down unable to tell the two apart.
    /// </summary>
    [Fact]
    public async Task WatchJob_WithATokenCancelledBeforeTheFirstRequest_ThrowsOperationCanceled()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK, Job, ("X-Nomad-Index", "7")));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in client.WatchJobAsync("web", cancellation.Token))
            {
            }
        });
    }
}
