using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snail.Toolkit.Nomad.Models;

/// <summary>One scheduling evaluation of a job; a blocked one with <see cref="FailedTaskGroups"/> means placement is impossible right now.</summary>
public sealed record Evaluation(
    [property: JsonPropertyName("ID")] string Id,
    string Status,
    string? StatusDescription = null,
    string? Type = null,
    string? TriggeredBy = null,
    [property: JsonPropertyName("FailedTGAllocs")] IReadOnlyDictionary<string, JsonElement>? FailedTaskGroupAllocations = null,
    long CreateTime = 0)
{
    /// <summary>Task groups the scheduler could not place, if any.</summary>
    public IReadOnlyCollection<string> FailedTaskGroups => FailedTaskGroupAllocations?.Keys.ToArray() ?? [];

    /// <summary>When the evaluation was created.</summary>
    public DateTimeOffset CreatedAt => NomadTime.FromNanoseconds(CreateTime);
}
