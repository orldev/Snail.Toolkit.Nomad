using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Snail.Toolkit.Nomad.Health;

/// <summary>Asks the agent to describe itself; healthy when it answers, with its version and leader in the data.</summary>
public sealed class NomadHealthCheck(INomadClient client) : IHealthCheck
{
    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var agent = await client.GetAgentAsync(cancellationToken);
            var leader = await client.GetLeaderAsync(cancellationToken);
            var data = new Dictionary<string, object>
            {
                ["version"] = agent.Version,
                ["name"] = agent.Name,
                ["leader"] = leader ?? ""
            };

            return leader is null
                ? HealthCheckResult.Degraded("Nomad has no cluster leader.", data: data)
                : HealthCheckResult.Healthy($"Nomad {agent.Version}", data);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or NomadException)
        {
            return HealthCheckResult.Unhealthy("Nomad is not reachable.", exception);
        }
    }
}
