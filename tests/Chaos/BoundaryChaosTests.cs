using System.Net;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Snail.Toolkit.Nomad.Health;

namespace Snail.Toolkit.Nomad.Tests.Chaos;

/// <summary>
/// The contract promises <see cref="NomadException"/> for every answer that is not what a call expects, whichever
/// path read it: two reads used to bypass the shared translation and let the serializer's own exception through.
/// </summary>
public sealed class BoundaryChaosTests
{
    private const string Html = "<html>a proxy answered</html>";

    /// <summary>A proxy in front of Nomad answers 200 with HTML; the leader read used to let the JsonException through.</summary>
    [Fact]
    public async Task GetLeader_WhenTheBodyIsNotJson_ThrowsNomadException()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK, Html));

        await Assert.ThrowsAsync<NomadException>(() => client.GetLeaderAsync());
    }

    /// <summary>One malformed answer used to kill a follower with a JsonException instead of the NomadException it catches.</summary>
    [Fact]
    public async Task WatchJob_WhenTheBodyIsNotJson_ThrowsNomadException()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK, Html, ("X-Nomad-Index", "7")));

        await Assert.ThrowsAsync<NomadException>(async () =>
        {
            await foreach (var _ in client.WatchJobAsync("web"))
            {
            }
        });
    }

    /// <summary>
    /// The probe answers the registered failure status for a malformed leader body; a JsonException escaping it made
    /// the health service report Unhealthy whatever status was registered.
    /// </summary>
    [Fact]
    public async Task Probe_WhenTheLeaderBodyIsNotJson_ReportsTheRegisteredFailureStatus()
    {
        var handler = new ByPathHandler("""{"member":{"Name":"srv-1"}}""", Html);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://nomad.test:4646/") };
        var check = new NomadHealthCheck(new NomadClient(http, Options.Create(new NomadOptions { Address = "http://nomad.test:4646" })));
        var registration = new HealthCheckRegistration("nomad", check, HealthStatus.Degraded, tags: null);

        var result = await check.CheckHealthAsync(new HealthCheckContext { Registration = registration });

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    /// <summary>A char is text to a caller; the renderer used to refuse it as a type without a literal.</summary>
    [Fact]
    public void Literal_WithAChar_QuotesIt()
    {
        Assert.Equal("\"x\"", HclVariables.Literal('x'));
    }

    private sealed class ByPathHandler(string agent, string leader) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var asksForLeader = request.RequestUri!.AbsolutePath.EndsWith("leader", StringComparison.Ordinal);

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(asksForLeader ? leader : agent),
                RequestMessage = request
            });
        }
    }
}
