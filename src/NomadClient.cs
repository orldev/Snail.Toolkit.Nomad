using System.Diagnostics.CodeAnalysis;
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
            var leader = await ReadAsync(Scoped(Get("v1/status/leader")), NomadJson.Default.String, cancellationToken).ConfigureAwait(false);

            return string.IsNullOrEmpty(leader) ? null : leader;
        }
        catch (NomadException failure) when (IsElection(failure))
        {
            return null;
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
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "A registration against an index is a contract of its own, answering null on a conflict; the two are told apart by the index, never by a defaulted argument.")]
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
    [SuppressMessage("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters", Justification = "A registration against an index is a contract of its own, answering null on a conflict; the two are told apart by the index, never by a defaulted argument.")]
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
    public async Task<NomadPage<JobStub>> ListJobsAsync(string? prefix = null, NomadPaging paging = default, CancellationToken cancellationToken = default) =>
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
    public async Task<NomadPage<Allocation>> ListAllocationsAsync(string jobId, NomadPaging paging = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return await ReadPageOrEmptyAsync(
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
    public async Task<NomadPage<Evaluation>> ListEvaluationsAsync(string jobId, NomadPaging paging = default, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return await ReadPageOrEmptyAsync(
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
    public async Task<NomadPage<NodeStub>> ListNodesAsync(NomadPaging paging = default, CancellationToken cancellationToken = default) =>
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
        catch (IOException failure)
        {
            throw new HttpRequestException("The client node dropped the connection while the log streamed.", failure);
        }
    }

    private ILogger Log => logger ?? NullLogger<NomadClient>.Instance;

    /// <remarks>
    /// Index zero asks Nomad to answer at once; every later request names the index the last answer
    /// carried, and Nomad holds it until something passes that index or the wait runs out. An answer
    /// at the same index is that wait running out, and says nothing changed. A cancelled token ends
    /// the sequence by throwing, so a caller can tell a shutdown from a purged job. With a
    /// <see cref="NomadOptions.WatchRetry"/> a lost Nomad is waited for and asked again from the same
    /// index; without one the failure is the caller's.
    /// </remarks>
    private async IAsyncEnumerable<T> WatchAsync<T>(
        string path, JsonTypeInfo<T> shape, [EnumeratorCancellation] CancellationToken cancellationToken)
        where T : class
    {
        var index = 0UL;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            (T Value, ulong Index)? change;
            try
            {
                change = await ReadAtIndexAsync(path, shape, index, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (options.Value.WatchRetry is { } retry && IsLostNomad(failure, cancellationToken))
            {
                NomadLog.WatchResumed(Log, failure, path, index, retry);
                await Task.Delay(retry, cancellationToken).ConfigureAwait(false);
                continue;
            }

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

    /// <remarks>
    /// The wait travels in milliseconds: Nomad reads a Go duration, and whole seconds truncated a
    /// WatchWait under one second to <c>0s</c>, which Nomad takes as its five-minute default.
    /// </remarks>
    private async Task<(T Value, ulong Index)?> ReadAtIndexAsync<T>(
        string path, JsonTypeInfo<T> shape, ulong index, CancellationToken cancellationToken)
        where T : class
    {
        var wait = (long)options.Value.WatchWait.TotalMilliseconds;
        var request = Scoped(Get(path))
            .Query("index", index.ToString(CultureInfo.InvariantCulture))
            .Query("wait", $"{wait}ms");

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
        catch (Exception failure) when (IsTransport(failure))
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

    /// <summary>Asks for one page instead of the whole list; a default <see cref="NomadPaging"/> asks for nothing and gets the list whole.</summary>
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
        catch (Exception failure) when (IsTransport(failure))
        {
            throw Translate(failure);
        }
    }

    /// <remarks>
    /// A list hanging off a job id answers 404 for a job Nomad does not know, and that is emptiness
    /// rather than the misdirection a 404 means on a list that takes no id.
    /// </remarks>
    private static async Task<NomadPage<T>> ReadPageOrEmptyAsync<T>(
        IHttpRequestBuilder request, JsonTypeInfo<List<T>> shape, CancellationToken cancellationToken)
    {
        try
        {
            return await ReadPageAsync(request, shape, cancellationToken).ConfigureAwait(false);
        }
        catch (NomadException failure) when (failure.StatusCode == HttpStatusCode.NotFound)
        {
            return new NomadPage<T>([]);
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
    /// misdirected client as an empty cluster. The body is read as a stream, as a page is: a list of
    /// five hundred jobs buffered whole was 500 KB on the large object heap for every call.
    /// </remarks>
    private static async Task<T> ReadAsync<T>(IHttpRequestBuilder request, JsonTypeInfo<T> shape, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            using var response = await request
                .SendCheckedAsync(HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            var value = await response.Content.ReadFromJsonAsync(shape, cancellationToken).ConfigureAwait(false);

            return value ?? throw new NomadException(HttpStatusCode.OK, $"Nomad returned an empty {typeof(T).Name}.");
        }
        catch (Exception failure) when (IsTransport(failure))
        {
            throw Translate(failure);
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

    private static bool IsElection(NomadException failure) =>
        failure.StatusCode == HttpStatusCode.InternalServerError
        && failure.Message.Contains("no cluster leader", StringComparison.OrdinalIgnoreCase);

    /// <summary>A failure a watch can wait out: no connection, a timeout that is not the caller's, or a server in trouble.</summary>
    /// <remarks>
    /// A 4xx is an answer about the request and stays final: a 403 will not become a 200 by waiting,
    /// and a 404 has already ended the watch before this is asked.
    /// </remarks>
    private static bool IsLostNomad(Exception failure, CancellationToken cancellationToken) =>
        failure switch
        {
            HttpRequestException => true,
            OperationCanceledException => !cancellationToken.IsCancellationRequested,
            NomadException answered => answered.StatusCode >= HttpStatusCode.InternalServerError,
            _ => false
        };

    /// <summary>What the transport and the serializer throw, and this client's contract does not name.</summary>
    private static bool IsTransport(Exception failure) =>
        failure is HttpBuilderException or JsonException or NotSupportedException;

    /// <remarks>
    /// The one place a failure of the transport becomes a <see cref="NomadException"/>: an error status
    /// keeps its status and Nomad's text, a body that is not the JSON a call expects reads as a 200
    /// that said nothing usable. Every read ends in this translation, so a path cannot forget it.
    /// </remarks>
    private static NomadException Translate(Exception failure)
    {
        if (failure is not HttpBuilderException answered)
        {
            return new NomadException(
                HttpStatusCode.OK, "Nomad returned a body that is not the JSON this call expects.", innerException: failure);
        }

        var status = answered.StatusCode ?? HttpStatusCode.InternalServerError;
        var message = string.IsNullOrWhiteSpace(answered.Body) ? $"Nomad answered {(int)status}." : answered.Body.Trim();

        return new NomadException(status, message, answered.Method, answered.RequestUri, answered);
    }

    private static string? Given(string? value) => string.IsNullOrEmpty(value) ? null : value;

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
