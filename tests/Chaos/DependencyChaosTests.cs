using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Snail.Toolkit.Nomad.Health;

namespace Snail.Toolkit.Nomad.Tests.Chaos;

/// <summary>
/// Nomad stalls, drops the connection, or is asked by sixty-four callers at once. The client keeps no state between
/// calls, so the parallel cases hold by construction; the failing dependency cases pin which exception reaches a caller.
/// </summary>
public sealed class DependencyChaosTests
{
    private const string Job = """
        {"ID":"web","Name":"web","Namespace":"default","Type":"service","Status":"running",
         "StatusDescription":"","Version":0,"Stop":false,"SubmitTime":1,"TaskGroups":[{"Name":"server","Count":1}]}
        """;

    /// <summary>An agent that never answers is cut by the client's Timeout, and the probe reports the registered status rather than hanging.</summary>
    [Fact]
    public async Task Probe_WhenTheAgentStalls_ReportsTheRegisteredFailureStatus()
    {
        var http = new HttpClient(new StallingHandler())
        {
            BaseAddress = new Uri("http://nomad.test:4646/"),
            Timeout = TimeSpan.FromMilliseconds(100)
        };
        var check = new NomadHealthCheck(new NomadClient(http, Options.Create(new NomadOptions { Address = "http://nomad.test:4646" })));
        var registration = new HealthCheckRegistration("nomad", check, HealthStatus.Degraded, tags: null);

        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = registration });

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    /// <summary>
    /// A client node that drops the connection while the tail streams used to surface as the stream's own
    /// IOException, which a log viewer catching the documented HttpRequestException never saw.
    /// </summary>
    [Fact]
    public async Task ReadLogs_WhenTheNodeDropsTheConnectionMidBody_ThrowsHttpRequestException()
    {
        var http = new HttpClient(new DroppingHandler()) { BaseAddress = new Uri("http://nomad.test:4646/") };
        var client = new NomadClient(http, Options.Create(new NomadOptions { Address = "http://nomad.test:4646" }));

        var failure = await Assert.ThrowsAsync<HttpRequestException>(() => client.ReadLogsAsync("a-1", "mcp", tailBytes: 4096));

        Assert.IsAssignableFrom<IOException>(failure.InnerException);
    }

    /// <summary>Sixty-four log tails through one client at once each arrive whole.</summary>
    [Fact]
    public async Task ReadLogs_WithSixtyFourCallersAtOnce_AnswersEachWhole()
    {
        var http = new HttpClient(new CountingHandler("last lines of the log")) { BaseAddress = new Uri("http://nomad.test:4646/") };
        var client = new NomadClient(http, Options.Create(new NomadOptions { Address = "http://nomad.test:4646" }));

        var tails = await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => client.ReadLogsAsync("a-1", "mcp", tailBytes: 64)));

        Assert.All(tails, tail => Assert.Equal("last lines of the log", tail));
    }

    /// <summary>An agent that loses every other request still feeds sixteen watchers their changes once a retry is set.</summary>
    [Fact]
    public async Task WatchJob_WithARetryAndAFlappingAgent_EveryWatcherStillSeesItsChanges()
    {
        var http = new HttpClient(new CountingHandler(Job, loseEveryOther: true)) { BaseAddress = new Uri("http://nomad.test:4646/") };
        var client = new NomadClient(http, Options.Create(new NomadOptions
        {
            Address = "http://nomad.test:4646",
            WatchRetry = TimeSpan.FromMilliseconds(1)
        }));

        var seen = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => TakeAsync(client.WatchJobAsync("web"), 3)));

        Assert.All(seen, jobs => Assert.Equal(3, jobs.Count));
    }

    /// <summary>Sixteen watchers on one client each follow their own sequence of indexes without crossing.</summary>
    [Fact]
    public async Task WatchJob_WithSixteenWatchersAtOnce_EachSeesItsOwnChanges()
    {
        var http = new HttpClient(new CountingHandler(Job)) { BaseAddress = new Uri("http://nomad.test:4646/") };
        var client = new NomadClient(http, Options.Create(new NomadOptions { Address = "http://nomad.test:4646" }));

        var seen = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => TakeAsync(client.WatchJobAsync("web"), 3)));

        Assert.All(seen, jobs => Assert.Equal(3, jobs.Count));
    }

    private static async Task<List<T>> TakeAsync<T>(IAsyncEnumerable<T> source, int count)
    {
        List<T> taken = [];
        await foreach (var item in source)
        {
            taken.Add(item);
            if (taken.Count == count)
                break;
        }

        return taken;
    }

    private sealed class StallingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request };
        }
    }

    private sealed class DroppingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new DroppedStream()),
                RequestMessage = request
            });
    }

    /// <summary>Hands over a few bytes, then fails the way a reset socket does.</summary>
    private sealed class DroppedStream : Stream
    {
        private bool _delivered;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_delivered)
                throw new IOException("Connection reset by peer");

            _delivered = true;
            buffer[offset] = (byte)'x';

            return 1;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>
    /// Answers every request with the same body and a fresh, increasing X-Nomad-Index, or loses every other one;
    /// safe to call from many threads.
    /// </summary>
    private sealed class CountingHandler(string body, bool loseEveryOther = false) : HttpMessageHandler
    {
        private long _index;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _index);
            if (loseEveryOther && index % 2 == 0)
                throw new HttpRequestException("Connection reset");

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body),
                RequestMessage = request
            };
            response.Headers.Add("X-Nomad-Index", index.ToString(System.Globalization.CultureInfo.InvariantCulture));

            return Task.FromResult(response);
        }
    }
}
