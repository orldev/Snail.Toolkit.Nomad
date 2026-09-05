using System.Text.Json.Nodes;
using Snail.Toolkit.Nomad.Models;

namespace Snail.Toolkit.Nomad;

/// <summary>
/// The Nomad HTTP API the platform needs: parse HCL2 jobs, register, read, list, scale and stop them,
/// read allocations, evaluations, nodes and task logs. Every call throws <see cref="NomadException"/>
/// on an error status; a read addressed at one object by its id answers null when Nomad does not
/// know it. A list endpoint takes no id, so its 404 is an error like any other rather than emptiness.
/// </summary>
public interface INomadClient
{
    /// <summary>The agent behind the configured address.</summary>
    Task<NomadAgent> GetAgentAsync(CancellationToken cancellationToken = default);

    /// <summary>Address of the current leader, or null while the cluster has none.</summary>
    Task<string?> GetLeaderAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Parses an HCL2 job into its JSON form. <paramref name="variables"/> is a var-file string
    /// (see <see cref="HclVariables.Render"/>); with <paramref name="canonicalize"/> the server fills defaults.
    /// </summary>
    Task<JsonObject> ParseJobAsync(string hcl, string? variables = null, bool canonicalize = true, CancellationToken cancellationToken = default);

    /// <summary>Registers (creates or updates) a job given in its JSON form, as returned by <see cref="ParseJobAsync"/>.</summary>
    Task<JobRegistration> RegisterJobAsync(JsonObject job, CancellationToken cancellationToken = default);

    /// <summary>The job, or null when Nomad does not know it.</summary>
    Task<NomadJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Jobs of the namespace, optionally only those whose id starts with <paramref name="prefix"/>.</summary>
    Task<IReadOnlyList<JobStub>> ListJobsAsync(string? prefix = null, CancellationToken cancellationToken = default);

    /// <summary>Allocation counts of the job by task group, or null when Nomad does not know it.</summary>
    Task<JobSummary?> GetJobSummaryAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Allocations of the job, newest first; empty for an unknown job.</summary>
    Task<IReadOnlyList<Allocation>> ListAllocationsAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Evaluations of the job, newest first; empty for an unknown job.</summary>
    Task<IReadOnlyList<Evaluation>> ListEvaluationsAsync(string jobId, CancellationToken cancellationToken = default);

    /// <summary>Sets the desired count of one task group. Nomad refuses while a deployment of the job is still active.</summary>
    Task<JobRegistration> ScaleJobAsync(string jobId, string taskGroup, int count, string? message = null, CancellationToken cancellationToken = default);

    /// <summary>Stops the job; with <paramref name="purge"/> it is removed from Nomad instead of staying as dead. Null when Nomad does not know it.</summary>
    Task<JobRegistration?> StopJobAsync(string jobId, bool purge = false, CancellationToken cancellationToken = default);

    /// <summary>Client nodes of the region.</summary>
    Task<IReadOnlyList<NodeStub>> ListNodesAsync(CancellationToken cancellationToken = default);

    /// <summary>The last <paramref name="tailBytes"/> of one task's stdout or stderr; empty when the allocation is gone or has no log yet.</summary>
    /// <remarks>
    /// A client node that cannot be reached throws instead of answering empty: a log viewer has to
    /// tell "this task said nothing" from "nobody could be asked".
    /// </remarks>
    Task<string> ReadLogsAsync(string allocationId, string task, LogType type = LogType.Stdout, int tailBytes = 4096, CancellationToken cancellationToken = default);
}
