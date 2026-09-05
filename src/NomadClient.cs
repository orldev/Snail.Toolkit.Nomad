using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Snail.Toolkit.Nomad.Models;

namespace Snail.Toolkit.Nomad;

/// <summary>
/// <see cref="INomadClient"/> over one <see cref="HttpClient"/> configured by AddNomad: the token travels
/// as a default header, namespace and region as query parameters of every request.
/// </summary>
public sealed class NomadClient(HttpClient http, IOptions<NomadOptions> options) : INomadClient
{
    /// <summary>Largest tail one log read may ask for.</summary>
    /// <remarks>
    /// The endpoint answers with as much as it is asked for, and the answer is buffered into a
    /// string, so an unbounded request is an unbounded allocation.
    /// </remarks>
    public const int MaxTailBytes = 1024 * 1024;

    /// <remarks>
    /// The two Respect flags make the records' own signatures the contract: a non-nullable member
    /// fed null, or a parameter without a default left unsent, fails the read instead of arriving
    /// as a null that the type says cannot happen and a computed property then dereferences.
    /// </remarks>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    /// <inheritdoc/>
    public async Task<NomadAgent> GetAgentAsync(CancellationToken cancellationToken = default)
    {
        var self = await GetAsync<AgentSelf>(Path("v1/agent/self"), cancellationToken).ConfigureAwait(false);
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
        using var response = await http.GetAsync(Path("v1/status/leader"), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.InternalServerError)
            return await LeaderOrFailureAsync(response, cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);
        var leader = await ReadAsync<string>(response, cancellationToken).ConfigureAwait(false);

        return string.IsNullOrEmpty(leader) ? null : leader;
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

        using var response = await http.PostAsJsonAsync(Path("v1/jobs/parse"), request, Json, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<JsonObject>(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<JobRegistration> RegisterJobAsync(JsonObject job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var request = new JsonObject { ["Job"] = job.DeepClone() };
        using var response = await http.PostAsJsonAsync(Path("v1/jobs"), request, Json, cancellationToken).ConfigureAwait(false);

        return await ReadRegistrationAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<NomadJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return await GetOrNullAsync<NomadJob>(Path($"v1/job/{Escape(jobId)}"), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobStub>> ListJobsAsync(string? prefix = null, CancellationToken cancellationToken = default) =>
        await GetAsync<List<JobStub>>(Path("v1/jobs", prefix is null ? null : ("prefix", prefix)), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<JobSummary?> GetJobSummaryAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        return await GetOrNullAsync<JobSummary>(Path($"v1/job/{Escape(jobId)}/summary"), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Allocation>> ListAllocationsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var allocations = await GetOrNullAsync<List<Allocation>>(Path($"v1/job/{Escape(jobId)}/allocations"), cancellationToken).ConfigureAwait(false);

        return allocations?.OrderByDescending(allocation => allocation.CreateTime).ToList() ?? [];
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Evaluation>> ListEvaluationsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        var evaluations = await GetOrNullAsync<List<Evaluation>>(Path($"v1/job/{Escape(jobId)}/evaluations"), cancellationToken).ConfigureAwait(false);

        return evaluations?.OrderByDescending(evaluation => evaluation.CreateTime).ToList() ?? [];
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

        using var response = await http.PostAsJsonAsync(Path($"v1/job/{Escape(jobId)}/scale"), request, Json, cancellationToken).ConfigureAwait(false);

        return await ReadRegistrationAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<JobRegistration?> StopJobAsync(string jobId, bool purge = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobId);

        using var response = await http.DeleteAsync(Path($"v1/job/{Escape(jobId)}", purge ? ("purge", "true") : null), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        return await ReadRegistrationAsync(response, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NodeStub>> ListNodesAsync(CancellationToken cancellationToken = default) =>
        await GetAsync<List<NodeStub>>(Path("v1/nodes"), cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    public async Task<string> ReadLogsAsync(string allocationId, string task, LogType type = LogType.Stdout, int tailBytes = 4096, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tailBytes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(tailBytes, MaxTailBytes);

        var path = Path($"v1/client/fs/logs/{Escape(allocationId)}",
            ("task", task),
            ("type", type == LogType.Stdout ? "stdout" : "stderr"),
            ("plain", "true"),
            ("origin", "end"),
            ("offset", tailBytes.ToString(CultureInfo.InvariantCulture)));

        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return string.Empty;

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadTailAsync(response, tailBytes, cancellationToken).ConfigureAwait(false);
    }

    /// <remarks>
    /// A 404 here says the path names no such object. On an endpoint that takes no identifier it
    /// says the address is not a Nomad agent, and answering with an empty list would report a
    /// misdirected client as an empty cluster.
    /// </remarks>
    private async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        using var response = await http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        using var response = await http.GetAsync(path, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<T>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<JobRegistration> ReadRegistrationAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        return await ReadAsync<JobRegistration>(response, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken) where T : class
    {
        try
        {
            return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken).ConfigureAwait(false)
                   ?? throw Failure(response, $"Nomad returned an empty {typeof(T).Name}.");
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            throw Failure(response, "Nomad returned a body that is not the JSON this call expects.", exception);
        }
    }

    private static async Task<string?> LeaderOrFailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        if (body.Contains("no cluster leader", StringComparison.OrdinalIgnoreCase))
            return null;

        throw Failure(response, body.Trim());
    }

    private static async Task<string> ReadTailAsync(HttpResponseMessage response, int tailBytes, CancellationToken cancellationToken)
    {
        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var reader = new StreamReader(stream);
            var buffer = new char[tailBytes];
            var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);

            return new string(buffer, 0, read);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);

        throw Failure(response, string.IsNullOrWhiteSpace(body) ? $"Nomad answered {(int)response.StatusCode}." : body.Trim());
    }

    /// <remarks>The status is the news; a body that cannot be read must not mask it.</remarks>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(stream);
                var buffer = new char[NomadException.BodyLimit];
                var read = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);

                return read < buffer.Length ? new string(buffer, 0, read) : $"{new string(buffer)}…";
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return string.Empty;
        }
    }

    private static NomadException Failure(HttpResponseMessage response, string message, Exception? cause = null) =>
        new(response.StatusCode, message, response.RequestMessage?.Method, response.RequestMessage?.RequestUri, cause);

    private string Path(string relative, params (string Name, string Value)?[] query)
    {
        List<string> parameters = [];
        var settings = options.Value;

        if (!string.IsNullOrEmpty(settings.Namespace))
            parameters.Add($"namespace={Escape(settings.Namespace)}");
        if (!string.IsNullOrEmpty(settings.Region))
            parameters.Add($"region={Escape(settings.Region)}");
        foreach (var parameter in query)
        {
            if (parameter is (var name, var value))
                parameters.Add($"{Escape(name)}={Escape(value)}");
        }

        return parameters.Count == 0 ? relative : $"{relative}?{string.Join('&', parameters)}";
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);
}
