using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Snail.Toolkit.Nomad.Extensions;
using Snail.Toolkit.Nomad.Health;

namespace Snail.Toolkit.Nomad.Tests;

public sealed class NomadHealthCheckTests
{
    private const string Self = """{"config":{"Version":{"Version":"1.9.3"}},"member":{"Name":"srv-1"}}""";

    [Fact]
    public async Task Probe_WhenTheAgentAnswersAndTheClusterHasALeader_IsHealthy()
    {
        var result = await ProbeAsync(new ByPathHandler(Self, "\"10.0.0.1:4647\""));

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal("1.9.3", result.Data["version"]);
        Assert.Equal("10.0.0.1:4647", result.Data["leader"]);
    }

    [Fact]
    public async Task Probe_WhileTheServersElectALeader_IsDegraded()
    {
        var result = await ProbeAsync(new ByPathHandler(Self, "No cluster leader", leaderStatus: HttpStatusCode.InternalServerError));

        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    /// <summary>The registered status used to be ignored in favour of a hardcoded Unhealthy.</summary>
    [Fact]
    public async Task Probe_WhenNomadFails_ReportsTheRegisteredFailureStatus()
    {
        var handler = new ByPathHandler("rpc error", "rpc error", HttpStatusCode.InternalServerError, HttpStatusCode.InternalServerError);

        var result = await ProbeAsync(handler, HealthStatus.Degraded);

        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.IsType<NomadException>(result.Exception);
    }

    [Fact]
    public void AddNomadHealthCheck_BeforeAddNomad_Throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddNomadHealthCheck());
    }

    [Fact]
    public void AddNomadHealthCheck_GivesTheProbeABudgetOfItsOwn()
    {
        var services = new ServiceCollection();
        services.AddNomad(TestConfiguration.Get()).AddNomadHealthCheck();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(5), Assert.Single(options.Registrations).Timeout);
    }

    private static async Task<HealthCheckResult> ProbeAsync(
        ByPathHandler handler, HealthStatus failureStatus = HealthStatus.Unhealthy)
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://nomad.test:4646/") };
        var check = new NomadHealthCheck(new NomadClient(http, Options.Create(new NomadOptions())));
        var registration = new HealthCheckRegistration("nomad", check, failureStatus, tags: null);

        return await check.CheckHealthAsync(new HealthCheckContext { Registration = registration });
    }

    /// <summary>Answers the agent and the leader calls apart, which one canned response cannot.</summary>
    private sealed class ByPathHandler(
        string agent,
        string leader,
        HttpStatusCode agentStatus = HttpStatusCode.OK,
        HttpStatusCode leaderStatus = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var asksForLeader = request.RequestUri!.AbsolutePath.EndsWith("leader", StringComparison.Ordinal);

            return Task.FromResult(new HttpResponseMessage(asksForLeader ? leaderStatus : agentStatus)
            {
                Content = new StringContent(asksForLeader ? leader : agent, Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }
}
