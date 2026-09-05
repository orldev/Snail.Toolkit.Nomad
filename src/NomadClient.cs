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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General)
    {
        PropertyNameCaseInsensitive = true
    };

    /// <inheritdoc/>
    public async Task<NomadAgent> GetAgentAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetDocumentAsync(Path("v1/agent/self"), cancellationToken)
                             ?? throw new NomadException(HttpStatusCode.NotFound, "The agent did not describe itself.");

        var root = document.RootElement;
        var config = root.GetProperty("config");
        var member = root.TryGetProperty("member", out var m) ? m : default;

        return new NomadAgent(
            member.ValueKind == JsonValueKind.Object && member.TryGetProperty("Name", out var name) ? name.GetString() ?? "" : "",
            config.TryGetProperty("Version", out var version) && version.TryGetProperty("Version", out var v) ? v.GetString() ?? "" : "",
            config.TryGetProperty("Datacenter", out var dc) ? dc.GetString() ?? "" : "",
            config.TryGetProperty("Region", out var region) ? region.GetString() ?? "" : "",
            config.TryGetProperty("Server", out var server) && server.TryGetProperty("Enabled", out var se) && se.GetBoolean(),
            config.TryGetProperty("Client", out var client) && client.TryGetProperty("Enabled", out var ce) && ce.GetBoolean());
    }

    /// <inheritdoc/>
    public async Task<string?> GetLeaderAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(Path("v1/status/leader"), cancellationToken);
        if (response.StatusCode == HttpStatusCode.InternalServerError)
            return null; // "No cluster leader" while the servers elect one

        await EnsureSuccessAsync(response, cancellationToken);
        var leader = await response.Content.ReadFromJsonAsync<string>(Json, cancellationToken);
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

        using var response = await http.PostAsJsonAsync(Path("v1/jobs/parse"), request, Json, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var job = await response.Content.ReadFromJsonAsync<JsonObject>(Json, cancellationToken);
        return job ?? throw new NomadException(response.StatusCode, "Nomad returned an empty job.");
    }

    /// <inheritdoc/>
    public async Task<JobRegistration> RegisterJobAsync(JsonObject job, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(job);

        var request = new JsonObject { ["Job"] = job.DeepClone() };
        using var response = await http.PostAsJsonAsync(Path("v1/jobs"), request, Json, cancellationToken);
        return await ReadRegistrationAsync(response, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<NomadJob?> GetJobAsync(string jobId, CancellationToken cancellationToken = default) =>
        await GetOrNullAsync<NomadJob>(Path($"v1/job/{Escape(jobId)}"), cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<JobStub>> ListJobsAsync(string? prefix = null, CancellationToken cancellationToken = default) =>
        await GetOrNullAsync<List<JobStub>>(Path("v1/jobs", prefix is null ? null : ("prefix", prefix)), cancellationToken) ?? [];

    /// <inheritdoc/>
    public async Task<JobSummary?> GetJobSummaryAsync(string jobId, CancellationToken cancellationToken = default) =>
        await GetOrNullAsync<JobSummary>(Path($"v1/job/{Escape(jobId)}/summary"), cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Allocation>> ListAllocationsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var allocations = await GetOrNullAsync<List<Allocation>>(Path($"v1/job/{Escape(jobId)}/allocations"), cancellationToken) ?? [];
        return allocations.OrderByDescending(allocation => allocation.CreateTime).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Evaluation>> ListEvaluationsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var evaluations = await GetOrNullAsync<List<Evaluation>>(Path($"v1/job/{Escape(jobId)}/evaluations"), cancellationToken) ?? [];
        return evaluations.OrderByDescending(evaluation => evaluation.CreateTime).ToList();
    }

    /// <inheritdoc/>
    public async Task<JobRegistration> ScaleJobAsync(string jobId, string taskGroup, int count, string? message = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskGroup);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var request = new JsonObject
        {
            ["Count"] = count,
            ["Target"] = new JsonObject { ["Group"] = taskGroup },
            ["Message"] = message
        };

        using var response = await http.PostAsJsonAsync(Path($"v1/job/{Escape(jobId)}/scale"), request, Json, cancellationToken);
        return await ReadRegistrationAsync(response, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<JobRegistration?> StopJobAsync(string jobId, bool purge = false, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync(Path($"v1/job/{Escape(jobId)}", purge ? ("purge", "true") : null), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        return await ReadRegistrationAsync(response, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<NodeStub>> ListNodesAsync(CancellationToken cancellationToken = default) =>
        await GetOrNullAsync<List<NodeStub>>(Path("v1/nodes"), cancellationToken) ?? [];

    /// <inheritdoc/>
    public async Task<string> ReadLogsAsync(string allocationId, string task, LogType type = LogType.Stdout, int tailBytes = 4096, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(allocationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(task);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(tailBytes);

        var path = Path($"v1/client/fs/logs/{Escape(allocationId)}",
            ("task", task),
            ("type", type == LogType.Stdout ? "stdout" : "stderr"),
            ("plain", "true"),
            ("origin", "end"),
            ("offset", tailBytes.ToString()));

        using var response = await http.GetAsync(path, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.InternalServerError)
            return string.Empty; // allocation gone, or the client that held it cannot be reached

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private async Task<T?> GetOrNullAsync<T>(string path, CancellationToken cancellationToken) where T : class
    {
        using var response = await http.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
    }

    private async Task<JsonDocument?> GetDocumentAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        await EnsureSuccessAsync(response, cancellationToken);
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }

    private static async Task<JobRegistration> ReadRegistrationAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JobRegistration>(Json, cancellationToken)
               ?? throw new NomadException(response.StatusCode, "Nomad returned an empty registration.");
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new NomadException(response.StatusCode, string.IsNullOrWhiteSpace(body) ? $"Nomad answered {(int)response.StatusCode}." : body.Trim());
    }

    private string Path(string relative, params (string Name, string Value)?[] query)
    {
        var parameters = new List<string>();
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
