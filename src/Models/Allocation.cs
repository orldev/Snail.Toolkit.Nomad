using System.Text.Json.Serialization;

namespace Snail.Toolkit.Nomad.Models;

/// <summary>State of one task inside an allocation.</summary>
public sealed record AllocationTaskState(
    string State = "",
    bool Failed = false,
    int Restarts = 0,
    DateTimeOffset StartedAt = default,
    DateTimeOffset FinishedAt = default);

/// <summary>One allocation of a job, from /v1/job/:id/allocations.</summary>
public sealed record Allocation(
    [property: JsonPropertyName("ID")] string Id,
    string Name,
    [property: JsonPropertyName("JobID")] string JobId,
    string TaskGroup,
    [property: JsonPropertyName("NodeID")] string NodeId,
    string NodeName = "",
    string ClientStatus = "",
    string DesiredStatus = "",
    long CreateTime = 0,
    long ModifyTime = 0,
    IReadOnlyDictionary<string, AllocationTaskState>? TaskStates = null)
{
    /// <summary>When the allocation was created.</summary>
    public DateTimeOffset CreatedAt => NomadTime.FromNanoseconds(CreateTime);

    /// <summary>When the allocation was last modified.</summary>
    public DateTimeOffset ModifiedAt => NomadTime.FromNanoseconds(ModifyTime);

    /// <summary>The allocation is running on a client right now.</summary>
    public bool IsRunning => string.Equals(ClientStatus, "running", StringComparison.OrdinalIgnoreCase);
}
