using System.Text.Json.Serialization;

namespace Snail.Toolkit.Nomad.Models;

/// <summary>Outcome of a job register, scale or stop call.</summary>
public sealed record JobRegistration(
    [property: JsonPropertyName("EvalID")] string EvalId,
    ulong JobModifyIndex = 0,
    string? Warnings = null);

/// <summary>Allocation counts of one task group.</summary>
public sealed record TaskGroupSummary(
    int Queued = 0,
    int Complete = 0,
    int Failed = 0,
    int Running = 0,
    int Starting = 0,
    int Lost = 0,
    int Unknown = 0)
{
    /// <summary>Allocations that are alive or about to be: running, starting or queued.</summary>
    public int Live => Running + Starting + Queued;
}

/// <summary>Allocation counts of a job by task group, from /v1/job/:id/summary and the job list.</summary>
/// <remarks>
/// <c>Summary</c> carries no default, so a body without it fails the read. The type exists to be
/// asked for counts, and a null there would surface as a dereference inside <c>Running</c>.
/// </remarks>
public sealed record JobSummary(
    [property: JsonPropertyName("JobID")] string JobId,
    string Namespace,
    IReadOnlyDictionary<string, TaskGroupSummary> Summary)
{
    /// <summary>Running allocations across every task group.</summary>
    public int Running => Summary.Values.Sum(group => group.Running);

    /// <summary>Failed or lost allocations across every task group.</summary>
    public int Failed => Summary.Values.Sum(group => group.Failed + group.Lost);
}

/// <summary>One job as the job list reports it.</summary>
public sealed record JobStub(
    [property: JsonPropertyName("ID")] string Id,
    string Name,
    string Namespace,
    string Type,
    string Status,
    string StatusDescription = "",
    bool Stop = false,
    long SubmitTime = 0,
    JobSummary? JobSummary = null)
{
    /// <summary>When the current version of the job was submitted.</summary>
    public DateTimeOffset SubmittedAt => NomadTime.FromNanoseconds(SubmitTime);
}

/// <summary>Name and desired count of one task group.</summary>
public sealed record TaskGroupStub(string Name, int Count = 0);

/// <summary>A registered job, from /v1/job/:id — the fields the platform reads; the full definition stays in Nomad.</summary>
public sealed record NomadJob(
    [property: JsonPropertyName("ID")] string Id,
    string Name,
    string Namespace,
    string Type,
    string Status,
    string StatusDescription,
    ulong Version,
    bool Stop,
    long SubmitTime,
    IReadOnlyList<TaskGroupStub> TaskGroups,
    IReadOnlyDictionary<string, string>? Meta = null)
{
    /// <summary>When the current version of the job was submitted.</summary>
    public DateTimeOffset SubmittedAt => NomadTime.FromNanoseconds(SubmitTime);

    /// <summary>Desired allocations across every task group.</summary>
    public int DesiredCount => TaskGroups.Sum(group => group.Count);
}

/// <summary>Nomad stamps times as nanoseconds since the Unix epoch.</summary>
public static class NomadTime
{
    /// <summary>Converts a Nomad nanosecond timestamp; zero and negatives map to <see cref="DateTimeOffset.MinValue"/>.</summary>
    public static DateTimeOffset FromNanoseconds(long nanoseconds) =>
        nanoseconds <= 0 ? DateTimeOffset.MinValue : DateTimeOffset.UnixEpoch.AddTicks(nanoseconds / 100);
}
