using System.Globalization;
using System.Net;
using System.Text;
using Microsoft.Extensions.Options;

namespace Snail.Toolkit.Nomad.Tests;

public sealed class NomadWatchTests
{
    private const string Job = """
        {"ID":"mcp-search","Name":"mcp-search","Namespace":"default","Type":"service","Status":"running",
         "StatusDescription":"","Version":0,"Stop":false,"SubmitTime":1,"TaskGroups":[{"Name":"server","Count":1}]}
        """;

    [Fact]
    public async Task WatchJob_WhenTheIndexDoesNotMove_YieldsNothingFurther()
    {
        var handler = new IndexedHandler(Job, [7, 7, 9]);
        var client = Client(handler);

        var seen = await TakeAsync(client.WatchJobAsync("mcp-search"), 2);

        Assert.Equal(2, seen.Count);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task WatchJob_AfterTheFirstAnswer_AsksFromTheIndexItCarried()
    {
        var handler = new IndexedHandler(Job, [7, 9]);
        var client = Client(handler);

        await TakeAsync(client.WatchJobAsync("mcp-search"), 2);

        Assert.Contains("index=0&wait=20000ms", handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains("index=7&wait=20000ms", handler.Requests[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WatchJob_WhenTheJobIsPurged_Ends()
    {
        var handler = new IndexedHandler(Job, [7], purgeAfter: 1);
        var client = Client(handler);

        var seen = await TakeAsync(client.WatchJobAsync("mcp-search"), 5);

        Assert.Single(seen);
    }

    /// <summary>Without an index there is nothing to follow, so the caller gets the state once.</summary>
    [Fact]
    public async Task WatchJob_WhenNothingCarriesAnIndex_YieldsOnceAndEnds()
    {
        var handler = new IndexedHandler(Job, [0]);
        var client = Client(handler);

        var seen = await TakeAsync(client.WatchJobAsync("mcp-search"), 5);

        Assert.Single(seen);
        Assert.Single(handler.Requests);
    }

    /// <summary>The second request finds no Nomad; with a retry the watch asks again from the index it had.</summary>
    [Fact]
    public async Task WatchJob_WithARetry_WhenNomadIsLost_ResumesFromItsIndex()
    {
        var handler = new IndexedHandler(Job, [7, 9], loseNomadAt: 1);
        var client = Client(handler, TimeSpan.FromMilliseconds(1));

        var seen = await TakeAsync(client.WatchJobAsync("mcp-search"), 2);

        Assert.Equal(2, seen.Count);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("index=7&", handler.Requests[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task WatchJob_WithoutARetry_WhenNomadIsLost_Throws()
    {
        var handler = new IndexedHandler(Job, [7, 9], loseNomadAt: 1);
        var client = Client(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => TakeAsync(client.WatchJobAsync("mcp-search"), 2));
    }

    /// <summary>The pause is the caller's token's to cut: a shutdown during it ends the watch at once.</summary>
    [Fact]
    public async Task WatchJob_WithARetry_WhenCancelledDuringThePause_Throws()
    {
        var handler = new IndexedHandler(Job, [7], loseNomadAt: 0);
        var client = Client(handler, TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TakeAsync(client.WatchJobAsync("mcp-search", cancellation.Token), 1));
    }

    /// <summary>A 403 is an answer, not a lost Nomad, and waiting would not change it.</summary>
    [Fact]
    public async Task WatchJob_WithARetry_WhenNomadRefuses_Throws()
    {
        var handler = new IndexedHandler(Job, [7], refuseAt: 0);
        var client = Client(handler, TimeSpan.FromMilliseconds(1));

        var failure = await Assert.ThrowsAsync<NomadException>(() => TakeAsync(client.WatchJobAsync("mcp-search"), 1));

        Assert.Equal(HttpStatusCode.Forbidden, failure.StatusCode);
    }

    private static NomadClient Client(IndexedHandler handler, TimeSpan? retry = null)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://nomad.test:4646/") };

        return new NomadClient(http, Options.Create(new NomadOptions { Address = "http://nomad.test:4646", WatchRetry = retry }));
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

    /// <summary>
    /// Answers with a scripted sequence of X-Nomad-Index values; 404 from <c>purgeAfter</c> on, no connection on the
    /// <c>loseNomadAt</c> turn, 403 on the <c>refuseAt</c> turn.
    /// </summary>
    private sealed class IndexedHandler(
        string body, ulong[] indexes, int purgeAfter = int.MaxValue, int loseNomadAt = -1, int refuseAt = -1) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var turn = Requests.Count;
            Requests.Add(request.RequestUri!.Query);

            if (turn == loseNomadAt)
                throw new HttpRequestException("Connection refused");

            if (turn == refuseAt)
                return Task.FromResult(Answer(HttpStatusCode.Forbidden, "Permission denied", null, request));

            if (turn >= purgeAfter)
                return Task.FromResult(Answer(HttpStatusCode.NotFound, "job not found", null, request));

            var answered = loseNomadAt >= 0 && turn > loseNomadAt ? turn - 1 : turn;
            var index = indexes[Math.Min(answered, indexes.Length - 1)];

            return Task.FromResult(Answer(HttpStatusCode.OK, body, index, request));
        }

        private static HttpResponseMessage Answer(HttpStatusCode status, string body, ulong? index, HttpRequestMessage request)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
                RequestMessage = request
            };
            if (index is > 0)
                response.Headers.Add("X-Nomad-Index", index.Value.ToString(CultureInfo.InvariantCulture));

            return response;
        }
    }
}
