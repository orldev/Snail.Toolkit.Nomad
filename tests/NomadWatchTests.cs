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

        Assert.Contains("index=0&wait=20s", handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains("index=7&wait=20s", handler.Requests[1], StringComparison.Ordinal);
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

    private static NomadClient Client(IndexedHandler handler)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://nomad.test:4646/") };

        return new NomadClient(http, Options.Create(new NomadOptions { Address = "http://nomad.test:4646" }));
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

    /// <summary>Answers with a scripted sequence of X-Nomad-Index values, then 404 if asked to.</summary>
    private sealed class IndexedHandler(string body, ulong[] indexes, int purgeAfter = int.MaxValue) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var turn = Requests.Count;
            Requests.Add(request.RequestUri!.Query);

            if (turn >= purgeAfter)
                return Task.FromResult(Answer(HttpStatusCode.NotFound, "job not found", null, request));

            var index = indexes[Math.Min(turn, indexes.Length - 1)];

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
