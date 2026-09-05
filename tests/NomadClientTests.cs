using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Snail.Toolkit.Nomad.Extensions;

namespace Snail.Toolkit.Nomad.Tests;

public sealed class NomadClientTests
{
    [Fact]
    public async Task Token_travels_as_header_and_namespace_as_query()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "[]");
        var client = RecordingHandler.Client(handler, options =>
        {
            options.Token = "secret";
            options.Namespace = "swarm";
        });

        await client.ListJobsAsync("mcp-");

        var request = Assert.Single(handler.Requests);
        Assert.Equal("http://nomad.test:4646/v1/jobs?namespace=swarm&prefix=mcp-", request.RequestUri!.ToString());
        Assert.Equal("secret", request.Headers.GetValues("X-Nomad-Token").Single());
    }

    [Fact]
    public async Task Parse_sends_hcl_and_var_file_string()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"ID":"mcp-search","TaskGroups":[{"Name":"server","Count":1}]}""");
        var client = RecordingHandler.Client(handler);

        var job = await client.ParseJobAsync("job \"mcp-search\" {}", HclVariables.Render(new Dictionary<string, object?> { ["port"] = 8803 }));

        var sent = JsonNode.Parse(handler.Bodies.Single())!.AsObject();
        Assert.Equal("job \"mcp-search\" {}", sent["JobHCL"]!.GetValue<string>());
        Assert.Equal("port = 8803\n", sent["Variables"]!.GetValue<string>());
        Assert.True(sent["Canonicalize"]!.GetValue<bool>());
        Assert.Equal("mcp-search", job["ID"]!.GetValue<string>());
    }

    [Fact]
    public async Task Register_wraps_the_job_and_reads_the_evaluation()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"EvalID":"e-1","JobModifyIndex":8,"Warnings":""}""");
        var client = RecordingHandler.Client(handler);

        var registration = await client.RegisterJobAsync(new JsonObject { ["ID"] = "mcp-search" });

        Assert.Equal("e-1", registration.EvalId);
        Assert.Equal(8ul, registration.JobModifyIndex);
        var sent = JsonNode.Parse(handler.Bodies.Single())!.AsObject();
        Assert.Equal("mcp-search", sent["Job"]!["ID"]!.GetValue<string>());
        Assert.Equal("http://nomad.test:4646/v1/jobs", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task Missing_job_reads_as_null()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.NotFound, "job not found"));

        Assert.Null(await client.GetJobAsync("nope"));
        Assert.Null(await client.GetJobSummaryAsync("nope"));
        Assert.Null(await client.StopJobAsync("nope"));
        Assert.Empty(await client.ListAllocationsAsync("nope"));
    }

    [Fact]
    public async Task Job_is_read_with_groups_and_times()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK, """
            {"ID":"mcp-search","Name":"mcp-search","Namespace":"default","Type":"service","Status":"running","StatusDescription":"",
             "Version":0,"Stop":false,"SubmitTime":1788434852078152633,"TaskGroups":[{"Name":"server","Count":2}],"Meta":null}
            """));

        var job = await client.GetJobAsync("mcp-search");

        Assert.NotNull(job);
        Assert.Equal("mcp-search", job.Id);
        Assert.Equal(2, job.DesiredCount);
        Assert.Equal(2026, job.SubmittedAt.Year);
    }

    [Fact]
    public async Task Job_list_carries_the_summary()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK, """
            [{"ID":"mcp-search","Name":"mcp-search","Namespace":"default","Type":"service","Status":"running","StatusDescription":"","Stop":false,
              "SubmitTime":1,"JobSummary":{"JobID":"mcp-search","Namespace":"default","Summary":{"server":{"Queued":1,"Complete":0,"Failed":0,"Running":2,"Starting":0,"Lost":0,"Unknown":0}}}}]
            """));

        var stub = Assert.Single(await client.ListJobsAsync());

        Assert.Equal(2, stub.JobSummary!.Running);
        Assert.Equal(3, stub.JobSummary.Summary["server"].Live);
    }

    [Fact]
    public async Task Error_status_becomes_a_nomad_exception_with_the_body()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.InternalServerError, "job scaling blocked due to active deployment"));

        var exception = await Assert.ThrowsAsync<NomadException>(() => client.ScaleJobAsync("mcp-search", "server", 2));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Equal("job scaling blocked due to active deployment", exception.Message);
    }

    [Fact]
    public async Task Stop_with_purge_adds_the_flag()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"EvalID":"e-2","JobModifyIndex":12}""");
        var client = RecordingHandler.Client(handler);

        await client.StopJobAsync("mcp-search", purge: true);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("http://nomad.test:4646/v1/job/mcp-search?purge=true", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task Logs_are_read_from_the_end_as_plain_text()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "last lines");
        var client = RecordingHandler.Client(handler);

        var text = await client.ReadLogsAsync("a-1", "mcp", Models.LogType.Stderr, 512);

        Assert.Equal("last lines", text);
        Assert.Equal("http://nomad.test:4646/v1/client/fs/logs/a-1?task=mcp&type=stderr&plain=true&origin=end&offset=512",
            handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public void AddNomad_binds_options_and_resolves_the_client()
    {
        var services = new ServiceCollection();
        services.AddNomad(TestConfiguration.Get("http://nomad.local:4646/", token: "t"));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<INomadClient>();

        Assert.IsType<NomadClient>(client);
    }

    [Fact]
    public void AddNomad_without_address_throws()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddNomad(TestConfiguration.Get(address: null)));
    }
}
