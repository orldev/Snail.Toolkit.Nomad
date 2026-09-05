using Microsoft.Extensions.DependencyInjection;
using Snail.Toolkit.Nomad.Extensions;

namespace Snail.Toolkit.Nomad.Tests;

public sealed class NomadIntegrationTests(NomadContainerFixture fixture) : IClassFixture<NomadContainerFixture>
{
    private const string Hcl = """
        variable "name" { type = string }
        variable "image" { type = string }
        variable "port" {
          type    = number
          default = 8080
        }

        job "mcp-NAME" {
          datacenters = ["dc1"]
          type        = "service"

          group "server" {
            count = 1

            network {
              port "mcp" { to = var.port }
            }

            task "mcp" {
              driver = "docker"
              config {
                image = var.image
                ports = ["mcp"]
              }
              resources {
                cpu    = 100
                memory = 64
              }
            }
          }
        }
        """;

    private static async Task<T> PollAsync<T>(Func<Task<T>> read, Func<T, bool> ready)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var value = await read();
        while (!ready(value) && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(250);
            value = await read();
        }

        return value;
    }

    private async Task<INomadClient> ClientAsync()
    {
        var services = new ServiceCollection();
        services.AddNomad(TestConfiguration.Get(await fixture.GetAddressAsync()));
        return services.BuildServiceProvider().GetRequiredService<INomadClient>();
    }

    [IntegrationFact]
    public async Task AgentAndLeader_AgainstALiveServer_Answer()
    {
        var client = await ClientAsync();

        var agent = await client.GetAgentAsync();
        var leader = await client.GetLeaderAsync();

        Assert.True(agent.ServerEnabled);
        Assert.False(agent.ClientEnabled);
        Assert.NotEmpty(agent.Version);
        Assert.NotNull(leader);
    }

    [IntegrationFact]
    public async Task Job_ParsedRegisteredAndStopped_RoundTripsThroughNomad()
    {
        var client = await ClientAsync();
        var name = "it-" + Guid.NewGuid().ToString("N")[..6];
        var jobId = "mcp-" + name;

        var job = await client.ParseJobAsync(
            Hcl.Replace("mcp-NAME", jobId, StringComparison.Ordinal),
            HclVariables.Render(new Dictionary<string, object?> { ["name"] = name, ["image"] = "alpine:3", ["port"] = 8803 }));
        Assert.Equal(jobId, job["ID"]!.GetValue<string>());
        Assert.Equal(8803, job["TaskGroups"]![0]!["Networks"]![0]!["DynamicPorts"]![0]!["To"]!.GetValue<int>());

        var registration = await client.RegisterJobAsync(job);
        Assert.NotEmpty(registration.EvalId);

        var read = await client.GetJobAsync(jobId);
        Assert.NotNull(read);
        Assert.Equal("service", read.Type);
        Assert.Equal(1, read.DesiredCount);

        var listed = await client.ListJobsAsync("mcp-it-");
        Assert.Contains(listed, stub => stub.Id == jobId);

        // Without a client node the allocation stays queued and the evaluation is blocked on the group;
        // the scheduler writes both a moment after the registration answers.
        var summary = await PollAsync(
            () => client.GetJobSummaryAsync(jobId),
            summary => summary?.Summary.GetValueOrDefault("server")?.Queued == 1);
        Assert.NotNull(summary);
        Assert.Equal(1, summary.Summary["server"].Queued);

        var evaluations = await PollAsync(
            () => client.ListEvaluationsAsync(jobId),
            evaluations => evaluations.Any(evaluation => evaluation.FailedTaskGroups.Contains("server")));
        Assert.Contains(evaluations, evaluation => evaluation.FailedTaskGroups.Contains("server"));
        Assert.Empty(await client.ListAllocationsAsync(jobId));
        Assert.Empty(await client.ListNodesAsync());

        Assert.NotNull(await client.StopJobAsync(jobId, purge: true));
        Assert.Null(await client.GetJobAsync(jobId));
    }
}
