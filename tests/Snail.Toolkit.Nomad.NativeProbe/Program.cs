using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Snail.Toolkit.Nomad;
using Snail.Toolkit.Nomad.Extensions;
using Snail.Toolkit.Nomad.Models;

var configuration = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Nomad:Address"] = "http://nomad.probe:4646",
        ["Nomad:Token"] = "secret",
        ["Nomad:Namespace"] = "shop",
        ["Nomad:Region"] = "eu",
        ["Nomad:Timeout"] = "00:00:40"
    })
    .Build();

var answers = new Answers();

await using var services = new ServiceCollection()
    .AddNomad(configuration, client => client.ConfigurePrimaryHttpMessageHandler(() => answers))
    .AddNomadHealthCheck()
    .BuildServiceProvider();

var nomad = services.GetRequiredService<INomadClient>();

var agent = await nomad.GetAgentAsync();
var leader = await nomad.GetLeaderAsync();
var jobs = await nomad.ListJobsAsync();
var page = await nomad.ListJobsAsync(paging: new NomadPaging(1));
var job = await nomad.GetJobAsync("web");
var summary = await nomad.GetJobSummaryAsync("web");
var allocations = await nomad.ListAllocationsAsync("web");
var watched = await nomad.WatchJobAsync("web").ToListAsync();
var parsed = await nomad.ParseJobAsync("job \"web\" {}");
var registration = await nomad.RegisterJobAsync(parsed);
var registered = answers.Bodies["/v1/jobs"];
var scaled = await nomad.ScaleJobAsync("web", "front", 3);
var stopped = await nomad.StopJobAsync("gone");
var tail = await nomad.ReadLogsAsync("a1", "server");
var health = await services.GetRequiredService<HealthCheckService>().CheckHealthAsync();
var hcl = HclVariables.Render(new Dictionary<string, object?> { ["image"] = "web:1", ["count"] = 3, ["dc"] = new[] { "dc1" } });

var isBound = answers.IsScoped && answers.Token == "secret";
var isRead = agent is { Name: "node-1", Version: "1.10.0", IsServerEnabled: true }
    && leader == "10.0.0.1:4647"
    && jobs.Items is [{ Id: "web", Status: "running" }]
    && page is { Items: [{ Id: "web" }], NextToken: "next" }
    && job is { Id: "web", DesiredCount: 3 }
    && summary is { Running: 2 }
    && allocations.Items is [{ Id: "old" }, { Id: "new" }]
    && watched is [{ Id: "web" }]
    && stopped is null
    && tail == "line one\nline two\n";
var isWritten = registration.EvalId == "eval-1"
    && JsonNode.Parse(registered)?["Job"]?["ID"]?.GetValue<string>() == "web"
    && scaled.EvalId == "eval-1"
    && answers.Bodies["/v1/job/web/scale"] == """{"Count":3,"Target":{"Group":"front"},"Message":null}"""
    && hcl == "count = 3\ndc = [\"dc1\"]\nimage = \"web:1\"\n";
var isHealthy = health.Status == HealthStatus.Healthy;

Console.WriteLine($"bound {isBound}, read {isRead}, written {isWritten}, healthy {isHealthy}");

return isBound && isRead && isWritten && isHealthy ? 0 : 1;

/// <summary>A Nomad agent answering by path from memory, keeping what was sent.</summary>
internal sealed class Answers : HttpMessageHandler
{
    private const string Job =
        """{"ID":"web","Name":"web","Namespace":"shop","Type":"service","Status":"running","StatusDescription":"","Version":2,"Stop":false,"SubmitTime":1760000000000000000,"TaskGroups":[{"Name":"front","Count":3}]}""";

    private const string Registration = """{"EvalID":"eval-1","JobModifyIndex":7,"Warnings":""}""";

    public Dictionary<string, string> Bodies { get; } = [];

    public bool IsScoped { get; private set; } = true;

    public string? Token { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;

        Token = request.Headers.TryGetValues("X-Nomad-Token", out var tokens) ? tokens.Single() : null;
        IsScoped &= query.Contains("namespace=shop", StringComparison.Ordinal) && query.Contains("region=eu", StringComparison.Ordinal);

        if (request.Content is not null)
            Bodies[path] = await request.Content.ReadAsStringAsync(cancellationToken);

        var (status, body) = path switch
        {
            "/v1/agent/self" => (HttpStatusCode.OK,
                """{"config":{"version":{"version":"1.10.0"},"datacenter":"dc1","region":"eu","server":{"enabled":true}},"member":{"name":"node-1"}}"""),
            "/v1/status/leader" => (HttpStatusCode.OK, "\"10.0.0.1:4647\""),
            "/v1/jobs" when request.Method == HttpMethod.Get => (HttpStatusCode.OK,
                """[{"ID":"web","Name":"web","Namespace":"shop","Type":"service","Status":"running"}]"""),
            "/v1/jobs" => (HttpStatusCode.OK, Registration),
            "/v1/jobs/parse" => (HttpStatusCode.OK, Job),
            "/v1/job/web" => (HttpStatusCode.OK, Job),
            "/v1/job/web/summary" => (HttpStatusCode.OK,
                """{"JobID":"web","Namespace":"shop","Summary":{"front":{"Running":2}}}"""),
            "/v1/job/web/allocations" => (HttpStatusCode.OK,
                """[{"ID":"old","Name":"web[0]","JobID":"web","TaskGroup":"front","NodeID":"n1","CreateTime":1},{"ID":"new","Name":"web[1]","JobID":"web","TaskGroup":"front","NodeID":"n2","CreateTime":2}]"""),
            "/v1/job/web/scale" => (HttpStatusCode.OK, Registration),
            "/v1/client/fs/logs/a1" => (HttpStatusCode.OK, "line one\nline two\n"),
            _ => (HttpStatusCode.NotFound, "not found")
        };

        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
            RequestMessage = request
        };

        if (path == "/v1/jobs" && query.Contains("per_page=1", StringComparison.Ordinal))
            response.Headers.Add("X-Nomad-NextToken", "next");

        return response;
    }
}
