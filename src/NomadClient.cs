using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Snail.Toolkit.HttpBuilder.Extensions;
using Snail.Toolkit.Nomad.Models;

namespace Snail.Toolkit.Nomad;

/// <summary>
/// <see cref="INomadClient"/> over one <see cref="HttpClient"/> configured by AddNomad: the token travels
/// as a default header, namespace and region as query parameters of every request.
/// </summary>
/// <remarks>
/// The transport is <see cref="TypedHttpClientBase"/>, so path joining, query escaping and the capped
/// error body come from one implementation shared with the other toolkit clients. Its
/// <see cref="HttpBuilderException"/> is translated at every boundary: the transport is an
/// implementation detail and must not surface in this client's contract.
/// </remarks>
public sealed class NomadClient(HttpClient http, IOptions<NomadOptions> options, ILogger<NomadClient>? logger = null)
    : TypedHttpClientBase(http), INomadClient
{
    /// <summary>Largest tail one log read may ask for.</summary>
    /// <remarks>
    /// The endpoint answers with as much as it is asked for, and the answer is buffered into a
    /// string, so an unbounded request is an unbounded allocation.
    /// </remarks>
    public const int MaxTailBytes = 1024 * 1024;

    /// <inheritdoc/>
    public async Task<NomadAgent> GetAgentAsync(CancellationToken cancellationToken = default)
    {
        var self = await ReadAsync(Scoped(Get("v1/agent/self")), NomadJson.Default.AgentSelf, cancellationToken).ConfigureAwait(false);
        var config = self.Config;

        return new NomadAgent(
            self.Member?.Name ?? "",
            config?.Version?.Version ?? "",
            config?.Datacenter ?? "",
            config?.Region ?? "",
            config?.Server?.Enabled ?? false,
            config?.Client?.Enabled ?? false);
    }

    /// <inheritdoc/>
    public async Task<string?> GetLeaderAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var leader = await Scoped(Get("v1/status/leader")).SendAsync(NomadJson.Default.String, cancellationToken).ConfigureAwait(false);

            return string.IsNullOrEmpty(leader) ? null : leader;
        }
        catch (HttpBuilderException failure) when (IsElection(failure))
        {
            return null;
        }
        catch (HttpBuilderException failure)
        {
            throw Translate(failure);
        }
    }

    /// <inheritdoc/>
    public async Task<JsonObject> ParseJobAsync(string hcl, string? variables = null, bool canonicalize = true, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hcl);

        var request = new JsonObject
        {
            ["JobHCL"] = hcl,
            ["Canonicalize"] = canonicalize
        };
        if (!string.IsNullOrEmpty(variables))
            request["Variables"] = variables;

        return await ReadAsync(
            Scoped(Post("v1/jobs/parse")).AsJson(request, NomadJson.Default.JsonObject),
            NomadJson.Default.JsonObject, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<JobRegistration> RegisterJobAsync(JsonObject job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var request = new JsonObject { ["Job"] = job.DeepClone() };

        var registration = await ReadAsync(
            Scoped(Post("v1/jobs")).AsJson(request, NomadJson.Default.JsonObject),
            NomadJson.Default.JobRegistration, cancellationToken).ConfigureAwait(false);

        NomadLog.JobRegistered(Log, job["ID"]?.ToString() ?? "", registration.EvalId);

        return registration;
    }

    /// <inheritdoc/>
    public async Task<JobRegistration?> RegisterJobAsync(JsonObject job, ulong expectedModifyIndex, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var request = new JsonObject
        {
            ["Job"] = job.DeepClone(),
            ["EnforceIndex"] = true,
            ["JobModifyIndex"] = expectedModifyIndex
        };

        try
        {
            var registration = await ReadAsync(
                Scoped(Post("v1/jobs")).AsJson(request, NomadJson.Default.JsonObject),
                NomadJson.Default.JobRegistration, cancellationToken).ConfigureAwait(false);

            NomadLog.JobRegistered(Log, job["ID"]?.ToString() ?? "", registration.EvalId);

            return registration;
        }
        catch (NomadException failure) when (IsIndexConflict(failure))
        {
            return null;
        }
    }

    /// <inheritdoc/>
    public async Task<NomadJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return await ReadOrNullAsync(
            Scoped(Get($"v1/job/{Escape(jobId)}")), NomadJson.Default.NomadJob, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobStub>> ListJobsAsync(string? prefix = null, CancellationToken cancellationToken = default) =>
        await ReadAsync(
            Scoped(Get("v1/jobs")).Query("prefix", prefix), NomadJson.Default.ListJobStub, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<NomadPage<JobStub>> ListJobsAsync(NomadPaging paging, string? prefix = null, CancellationToken cancellationToken = default) =>
        await ReadPageAsync(Paged(Scoped(Get("v1/jobs")).Query("prefix", prefix), paging), NomadJson.Default.ListJobStub, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public IAsyncEnumerable<NomadJob> WatchJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return WatchAsync($"v1/job/{Escape(jobId)}", NomadJson.Default.NomadJob, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<JobSummary?> GetJobSummaryAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return await ReadOrNullAsync(
            Scoped(Get($"v1/job/{Escape(jobId)}/summary")), NomadJson.Default.JobSummary, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Allocation>> ListAllocationsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var allocations = await ReadOrNullAsync(
            Scoped(Get($"v1/job/{Escape(jobId)}/allocations")), NomadJson.Default.ListAllocation, cancellationToken).ConfigureAwait(false);

        return allocations?.OrderByDescending(allocation => allocation.CreateTime).ToList() ?? [];
    }

    /// <inheritdoc/>
    public async Task<NomadPage<Allocation>> ListAllocationsAsync(string jobId, NomadPaging paging, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return await ReadPageAsync(
            Paged(Scoped(Get($"v1/job/{Escape(jobId)}/allocations")), paging),
            NomadJson.Default.ListAllocation, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public IAsyncEnumerable<IReadOnlyList<Allocation>> WatchAllocationsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return WatchAsync($"v1/job/{Escape(jobId)}/allocations", NomadJson.Default.ListAllocation, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Evaluation>> ListEvaluationsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var evaluations = await ReadOrNullAsync(
            Scoped(Get($"v1/job/{Escape(jobId)}/evaluations")), NomadJson.Default.ListEvaluation, cancellationToken).ConfigureAwait(false);

        return evaluations?.OrderByDescending(evaluation => evaluation.CreateTime).ToList() ?? [];
    }

    /// <inheritdoc/>
    public async Task<NomadPage<Evaluation>> ListEvaluationsAsync(string jobId, NomadPaging paging, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return await ReadPageAsync(
            Paged(Scoped(Get($"v1/job/{Escape(jobId)}/evaluations")), paging),
            NomadJson.Default.ListEvaluation, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<JobRegistration> ScaleJobAsync(string jobId, string taskGroup, int count, string? message = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskGroup);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var request = new JsonObject
        {
            ["Count"] = count,
            ["Target"] = new JsonObject { ["Group"] = taskGroup },
            ["Message"] = message
        };

        var registration = await ReadAsync(
            Scoped(Post($"v1/job/{Escape(jobId)}/scale")).AsJson(request, NomadJson.Default.JsonObject),
            NomadJson.Default.JobRegistration, cancellationToken).ConfigureAwait(false);

        NomadLog.JobScaled(Log, jobId, taskGroup, count, registration.EvalId);

        return registration;
    }

    /// <inheritdoc/>
    public async Task<JobRegistration?> StopJobAsync(string jobId, bool purge = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var request = Scoped(Delete($"v1/job/{Escape(jobId)}")).Query("purge", purge ? "true" : null);

        var registration = await ReadOrNullAsync(request, NomadJson.Default.JobRegistration, cancellationToken).ConfigureAwait(false);
        if (registration is not null)
            NomadLog.JobStopped(Log, jobId, purge, registration.EvalId);

        return registration;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NodeStub>> ListNodesAsync(CancellationToken cancellationToken = default) =>
        await ReadAsync(Scoped(Get("v1/nodes")), NomadJson.Default.ListNodeStub, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<NomadPage<NodeStub>> ListNodesAsync(NomadPaging paging, CancellationToken cancellationToken = default) =>
        await ReadPageAsync(Paged(Scoped(Get("v1/nodes")), paging), NomadJson.Default.ListNodeStub, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<string> ReadLogsAsync(string allocationId, string task, LogType type = LogType.Stdout, int tailBytes = 4096, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tailBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tailBytes, MaxTailBytes);

        var request = Scoped(Get($"v1/client/fs/logs/{Escape(allocationId)}"))
            .Query("task", task)
            .Query("type", type == LogType.Stdout ? "stdout" : "stderr")
            .Query("plain", "true")
            .Query("origin", "end")
            .Query("offset", tailBytes.ToString(CultureInfo.InvariantCulture));

        try
        {
            using var response = await request
                .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            return await ReadTailAsync(response, tailBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpBuilderException failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            return string.Empty;
        }
        catch (HttpBuilderException failure)
        {
            throw Translate(failure);
        }
    }

    private ILogger Log => logger ?? NullLogger<NomadClient>.Instance;

    /// <remarks>
    /// Index zero asks Nomad to answer at once; every later request names the index the last answer
    /// carried, and Nomad holds it until something passes that index or the wait runs out. An answer
    /// at the same index is that wait running out, and says nothing changed.
    /// </remarks>
    private async IAsyncEnumerable<T> WatchAsync<T>(
        string path, JsonTypeInfo<T> shape, [EnumeratorCancellation] CancellationToken cancellationToken)
        where T : class
    {
        var index = 0UL;
        while (!cancellationToken.IsCancellationRequested)
        {
            var change = await ReadAtIndexAsync(path, shape, index, cancellationToken).ConfigureAwait(false);
            if (change is null)
                yield break;

            var (value, answered) = change.Value;
            if (answered == 0)
            {
                yield return value;
                yield break;
            }

            if (answered < index)
                index = 0;
            if (answered == index)
                continue;

            index = answered;
            yield return value;
        }
    }

    private async Task<(T Value, ulong Index)?> ReadAtIndexAsync<T>(
        string path, JsonTypeInfo<T> shape, ulong index, CancellationToken cancellationToken)
        where T : class
    {
        var wait = (int)options.Value.WatchWait.TotalSeconds;
        var request = Scoped(Get(path))
            .Query("index", index.ToString(CultureInfo.InvariantCulture))
            .Query("wait", $"{wait}s");

        try
        {
            using var response = await request
                .SendCheckedAsync(HttpCompletionOption.ResponseContentRead, cancellationToken).ConfigureAwait(false);

            var value = await response.Content.ReadFromJsonAsync(shape, cancellationToken).ConfigureAwait(false);

            return value is null ? null : (value, Index(response));
        }
        catch (HttpBuilderException failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (HttpBuilderException failure)
        {
            throw Translate(failure);
        }
    }

    /// <remarks>Zero says the answer carried no index, so there is nothing to follow.</remarks>
    private static ulong Index(HttpResponseMessage response) =>
        response.Headers.TryGetValues("X-Nomad-Index", out var values)
        && ulong.TryParse(values.FirstOrDefault(), CultureInfo.InvariantCulture, out var index)
            ? index
            : 0;

    /// <summary>Asks for one page instead of the whole list.</summary>
    private static IHttpRequestBuilder Paged(IHttpRequestBuilder request, NomadPaging paging) =>
        request
            .Query("per_page", paging.PageSize > 0 ? paging.PageSize.ToString(CultureInfo.InvariantCulture) : null)
            .Query("next_token", paging.Token);

    /// <remarks>
    /// Read with the headers rather than through the typed terminal: the continuation token lives
    /// in <c>X-Nomad-NextToken</c>, which a deserialized body cannot carry.
    /// </remarks>
    private static async Task<NomadPage<T>> ReadPageAsync<T>(
        IHttpRequestBuilder request, JsonTypeInfo<List<T>> shape, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await request
                .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            var items = await response.Content.ReadFromJsonAsync(shape, cancellationToken).ConfigureAwait(false) ?? [];
            var next = response.Headers.TryGetValues("X-Nomad-NextToken", out var values) ? values.FirstOrDefault() : null;

            return new NomadPage<T>(items, string.IsNullOrEmpty(next) ? null : next);
        }
        catch (HttpBuilderException failure)
        {
            throw Translate(failure);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new NomadException(
                HttpStatusCode.OK, "Nomad returned a body that is not the JSON this call expects.", innerException: exception);
        }
    }

    /// <summary>Puts the configured namespace and region on a request; an unset one is skipped.</summary>
    private IHttpRequestBuilder Scoped(IHttpRequestBuilder request)
    {
        var settings = options.Value;

        return request
            .Query("namespace", Given(settings.Namespace))
            .Query("region", Given(settings.Region));
    }

    /// <remarks>
    /// The shape is the generated metadata of <see cref="NomadJson"/>, so the models survive trimming
    /// and their own signatures stay the contract: a non-nullable member fed null, or a parameter
    /// without a default left unsent, fails the read instead of arriving as a null the type forbids.
    /// A 404 here says the path names no such object. On an endpoint that takes no identifier it
    /// says the address is not a Nomad agent, and answering with an empty list would report a
    /// misdirected client as an empty cluster.
    /// </remarks>
    private static async Task<T> ReadAsync<T>(IHttpRequestBuilder request, JsonTypeInfo<T> shape, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await request.SendAsync(shape, cancellationToken).ConfigureAwait(false)
                   ?? throw new NomadException(HttpStatusCode.OK, $"Nomad returned an empty {typeof(T).Name}.");
        }
        catch (HttpBuilderException failure)
        {
            throw Translate(failure);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw new NomadException(
                HttpStatusCode.OK, "Nomad returned a body that is not the JSON this call expects.", innerException: exception);
        }
    }

    private static async Task<T?> ReadOrNullAsync<T>(IHttpRequestBuilder request, JsonTypeInfo<T> shape, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await ReadAsync(request, shape, cancellationToken).ConfigureAwait(false);
        }
        catch (NomadException failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<string> ReadTailAsync(HttpResponseMessage response, int tailBytes, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await LogTail.ReadAsync(stream, tailBytes, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <remarks>
    /// Nomad reports a refused enforce-index as a plain 500 whose text names the index, so the text
    /// is the only signal there is. Matched loosely on purpose: the wording has moved between
    /// versions, the index numbers in it have not.
    /// </remarks>
    private static bool IsIndexConflict(NomadException failure) =>
        failure.Message.Contains("job modify index", StringComparison.OrdinalIgnoreCase);

    private static bool IsElection(HttpBuilderException failure) =>
        failure.StatusCode == HttpStatusCode.InternalServerError
        && failure.Body?.Contains("no cluster leader", StringComparison.OrdinalIgnoreCase) == true;

    private static NomadException Translate(HttpBuilderException failure)
    {
        var status = failure.StatusCode ?? HttpStatusCode.InternalServerError;
        var message = string.IsNullOrWhiteSpace(failure.Body) ? $"Nomad answered {(int)status}." : failure.Body.Trim();

        return new NomadException(status, message, failure.Method, failure.RequestUri, failure);
    }

    private static string? Given(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
