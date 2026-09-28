using System.Text.Json.Serialization;

namespace Snail.Toolkit.Nomad.Models;

/// <summary>State of one task inside an allocation.</summary>
/// <remarks>
/// Both times are null while they have not happened: Nomad 2.0.7 sends <c>"FinishedAt": null</c> for a running task and
/// <c>"StartedAt": null</c> for a pending one, and a non-nullable time failed the whole allocation list on them.
/// </remarks>
public sealed record AllocationTaskState(
    string State = "",
    bool Failed = false,
    int Restarts = 0,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? FinishedAt = null);

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
