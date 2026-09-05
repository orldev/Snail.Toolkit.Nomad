using System.Text.Json.Serialization;

namespace Snail.Toolkit.Nomad.Models;

/// <summary>One client node as the node list reports it.</summary>
public sealed record NodeStub(
    [property: JsonPropertyName("ID")] string Id,
    string Name,
    string Datacenter = "",
    string NodeClass = "",
    string Status = "",
    string StatusDescription = "",
    bool Drain = false,
    string SchedulingEligibility = "",
    string Version = "",
    string Address = "")
{
    /// <summary>The node is up and accepts placements.</summary>
    public bool IsReady =>
        string.Equals(Status, "ready", StringComparison.OrdinalIgnoreCase)
        && !Drain
        && string.Equals(SchedulingEligibility, "eligible", StringComparison.OrdinalIgnoreCase);
}
