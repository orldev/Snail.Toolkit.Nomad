using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Snail.Toolkit.Nomad.Extensions;
using Snail.Toolkit.Nomad.Models;

namespace Snail.Toolkit.Nomad.Tests;

public sealed class NomadClientTests
{
    [Fact]
    public async Task Request_WithATokenAndANamespace_SendsTheHeaderAndTheQuery()
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
    public async Task ParseJob_WithVariables_SendsTheHclAndTheVarFile()
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
    public async Task RegisterJob_WithAParsedJob_WrapsItAndReadsTheEvaluation()
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
    public async Task ReadsByJobId_WhenNomadDoesNotKnowIt_AnswerNull()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.NotFound, "job not found"));

        Assert.Null(await client.GetJobAsync("nope"));
        Assert.Null(await client.GetJobSummaryAsync("nope"));
        Assert.Null(await client.StopJobAsync("nope"));
        Assert.Empty(await client.ListAllocationsAsync("nope"));
    }

    /// <summary>Nomad 2.0.7, a running task: FinishedAt and LastRestart are null, and the list failed whole on them.</summary>
    [Fact]
    public async Task ListAllocations_RunningTask_ReadsItWithNoFinishTime()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK, """
            [{"ID":"0ba5b63e","Name":"mcp-sandbox.server[0]","JobID":"mcp-sandbox","TaskGroup":"server","NodeID":"03e76242",
              "ClientStatus":"running","DesiredStatus":"run","CreateTime":1788434852078152633,"ModifyTime":1788434852078152633,
              "TaskStates":{"mcp":{"State":"running","Failed":false,"Restarts":0,"LastRestart":null,
                                   "StartedAt":"2026-09-28T12:52:03.150877751Z","FinishedAt":null}}}]
            """));

        var state = Assert.Single(await client.ListAllocationsAsync("mcp-sandbox")).TaskStates!["mcp"];

        Assert.Equal("running", state.State);
        Assert.NotNull(state.StartedAt);
        Assert.Null(state.FinishedAt);
    }

    [Fact]
    public async Task GetJob_WithGroupsAndATimestamp_ReadsThemBack()
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
    public async Task ListJobs_WhenAStubCarriesASummary_ReadsTheCounts()
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
    public async Task Call_WithAnErrorStatus_ThrowsNomadExceptionCarryingTheBody()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.InternalServerError, "job scaling blocked due to active deployment"));

        var exception = await Assert.ThrowsAsync<NomadException>(() => client.ScaleJobAsync("mcp-search", "server", 2));

        Assert.Equal(HttpStatusCode.InternalServerError, exception.StatusCode);
        Assert.Equal("job scaling blocked due to active deployment", exception.Message);
    }

    [Fact]
    public async Task StopJob_WithPurge_AddsTheFlag()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"EvalID":"e-2","JobModifyIndex":12}""");
        var client = RecordingHandler.Client(handler);

        await client.StopJobAsync("mcp-search", purge: true);

        var request = handler.Requests.Single();
        Assert.Equal(HttpMethod.Delete, request.Method);
        Assert.Equal("http://nomad.test:4646/v1/job/mcp-search?purge=true", request.RequestUri!.ToString());
    }

    [Fact]
    public async Task ReadLogs_WithATail_ReadsFromTheEndAsPlainText()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "last lines");
        var client = RecordingHandler.Client(handler);

        var text = await client.ReadLogsAsync("a-1", "mcp", Models.LogType.Stderr, 512);

        Assert.Equal("last lines", text);
        Assert.Equal("http://nomad.test:4646/v1/client/fs/logs/a-1?task=mcp&type=stderr&plain=true&origin=end&offset=512",
            handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public void AddNomad_WithAConfiguration_ResolvesTheClient()
    {
        var services = new ServiceCollection();
        services.AddNomad(TestConfiguration.Get("http://nomad.local:4646/", token: "t"));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<INomadClient>();

        Assert.IsType<NomadClient>(client);
    }

    [Fact]
    public void AddNomad_RegistersEachHalfOfTheContract()
    {
        var services = new ServiceCollection();
        services.AddNomad(TestConfiguration.Get());

        using var provider = services.BuildServiceProvider();

        Assert.IsType<NomadClient>(provider.GetRequiredService<INomadStatus>());
        Assert.IsType<NomadClient>(provider.GetRequiredService<INomadJobs>());
        Assert.IsType<NomadClient>(provider.GetRequiredService<INomadScheduling>());
        Assert.IsType<NomadClient>(provider.GetRequiredService<INomadNodes>());
        Assert.IsType<NomadClient>(provider.GetRequiredService<INomadLogs>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nomad.local:4646")]
    [InlineData("ftp://nomad.local")]
    public void AddNomad_WithAnAddressThatIsNotAnHttpUrl_FailsValidation(string? address)
    {
        var services = new ServiceCollection();
        services.AddNomad(TestConfiguration.Get(address));

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<NomadOptions>>().Value);
    }

    [Fact]
    public void AddNomad_WithATimeoutThatIsNotPositive_FailsValidation()
    {
        var services = new ServiceCollection();
        services.AddNomad(options =>
        {
            options.Address = "http://nomad.local:4646";
            options.Timeout = TimeSpan.Zero;
        });

        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<NomadOptions>>().Value);
    }

    /// <summary>An address that is not a Nomad agent answers 404 to everything.</summary>
    [Fact]
    public async Task ListJobs_WhenTheEndpointIsNotFound_Throws()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.NotFound, "404 page not found"));

        var exception = await Assert.ThrowsAsync<NomadException>(() => client.ListJobsAsync());

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
    }

    [Fact]
    public async Task ListNodes_WhenTheEndpointIsNotFound_Throws()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.NotFound, "404 page not found"));

        await Assert.ThrowsAsync<NomadException>(() => client.ListNodesAsync());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task GetJob_WithABlankId_Throws(string jobId)
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK, "{}"));

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetJobAsync(jobId));
    }

    [Fact]
    public async Task ReadLogs_WithATailBeyondTheCap_Throws()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK, "text"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => client.ReadLogsAsync("a-1", "mcp", tailBytes: NomadClient.MaxTailBytes + 1));
    }

    [Fact]
    public async Task GetLeader_WhileTheServersElectOne_ReadsAsNull()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.InternalServerError, "No cluster leader"));

        Assert.Null(await client.GetLeaderAsync());
    }

    /// <summary>Nomad answers 500 for a failed RPC too, and that is not an election.</summary>
    [Fact]
    public async Task GetLeader_WhenTheCallFailsForAnotherReason_Throws()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.InternalServerError, "rpc error: EOF"));

        var exception = await Assert.ThrowsAsync<NomadException>(() => client.GetLeaderAsync());

        Assert.Equal("rpc error: EOF", exception.Message);
    }

    [Fact]
    public async Task Failure_CarriesTheMethodAndTheAddress()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.Conflict, "conflict"));

        var exception = await Assert.ThrowsAsync<NomadException>(() => client.StopJobAsync("mcp-search"));

        Assert.Equal(HttpMethod.Delete, exception.Method);
        Assert.Equal("http://nomad.test:4646/v1/job/mcp-search", exception.RequestUri!.ToString());
    }

    [Fact]
    public async Task Failure_WithABodyBeyondTheLimit_CutsIt()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.BadRequest, new string('x', 20_000)));

        var exception = await Assert.ThrowsAsync<NomadException>(() => client.ListJobsAsync());

        Assert.True(exception.Message.Length < 5_000, $"the body was kept whole at {exception.Message.Length} characters");
        Assert.EndsWith("…", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadingJson_WhenTheBodyIsNotWhatTheCallExpects_ThrowsNomadException()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK, "<html>a proxy answered</html>"));

        await Assert.ThrowsAsync<NomadException>(() => client.ListJobsAsync());
    }

    /// <summary>A null there used to arrive as one and surface later as a dereference in Running.</summary>
    [Fact]
    public async Task GetJobSummary_WhenTheCountsAreNull_Throws()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK,
            """{"JobID":"mcp-search","Namespace":"default","Summary":null}"""));

        await Assert.ThrowsAsync<NomadException>(() => client.GetJobSummaryAsync("mcp-search"));
    }

    [Fact]
    public async Task GetJob_WithoutTheTaskGroups_Throws()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK,
            """
            {"ID":"mcp-search","Name":"mcp-search","Namespace":"default","Type":"service","Status":"running",
             "StatusDescription":"","Version":0,"Stop":false,"SubmitTime":1}
            """));

        await Assert.ThrowsAsync<NomadException>(() => client.GetJobAsync("mcp-search"));
    }

    [Fact]
    public async Task RegisterJob_AgainstAnIndex_EnforcesIt()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, """{"EvalID":"e-3","JobModifyIndex":9}""");
        var client = RecordingHandler.Client(handler);

        var registration = await client.RegisterJobAsync(new JsonObject { ["ID"] = "mcp-search" }, expectedModifyIndex: 8);

        Assert.NotNull(registration);
        var sent = JsonNode.Parse(handler.Bodies.Single())!.AsObject();
        Assert.True(sent["EnforceIndex"]!.GetValue<bool>());
        Assert.Equal(8ul, sent["JobModifyIndex"]!.GetValue<ulong>());
    }

    /// <summary>Someone else changed the job since it was read, so this deployment must not land.</summary>
    [Fact]
    public async Task RegisterJob_AgainstAStaleIndex_AnswersNull()
    {
        var client = RecordingHandler.Client(new RecordingHandler(
            HttpStatusCode.InternalServerError,
            "Enforcing job modify index 8: job exists with conflicting job modify index: 11"));

        Assert.Null(await client.RegisterJobAsync(new JsonObject { ["ID"] = "mcp-search" }, expectedModifyIndex: 8));
    }

    [Fact]
    public async Task RegisterJob_AgainstAnIndex_WhenNomadFailsForAnotherReason_Throws()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.InternalServerError, "rpc error: EOF"));

        await Assert.ThrowsAsync<NomadException>(
            () => client.RegisterJobAsync(new JsonObject { ["ID"] = "mcp-search" }, expectedModifyIndex: 8));
    }

    [Fact]
    public async Task ListJobs_WithPaging_AsksForOnePageAndReadsTheContinuation()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "[]", ("X-Nomad-NextToken", "eyJ0IjoxfQ"));
        var client = RecordingHandler.Client(handler);

        var page = await client.ListJobsAsync(new NomadPaging(50), "mcp-");

        Assert.Equal("eyJ0IjoxfQ", page.NextToken);
        Assert.True(page.HasMore);
        Assert.Equal("http://nomad.test:4646/v1/jobs?prefix=mcp-&per_page=50", handler.Requests.Single().RequestUri!.ToString());
    }

    [Fact]
    public async Task ListNodes_OnTheLastPage_HasNoContinuation()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "[]");
        var client = RecordingHandler.Client(handler);

        var page = await client.ListNodesAsync(new NomadPaging(10, "eyJ0IjoxfQ"));

        Assert.Null(page.NextToken);
        Assert.False(page.HasMore);
        Assert.Equal("http://nomad.test:4646/v1/nodes?per_page=10&next_token=eyJ0IjoxfQ", handler.Requests.Single().RequestUri!.ToString());
    }

    /// <summary>An ACL that hides the configuration still lets the agent answer.</summary>
    [Fact]
    public async Task GetAgent_WithoutAConfigSection_ReadsTheFieldsAsEmpty()
    {
        var client = RecordingHandler.Client(new RecordingHandler(HttpStatusCode.OK, """{"member":{"Name":"srv-1"}}"""));

        var agent = await client.GetAgentAsync();

        Assert.Equal("srv-1", agent.Name);
        Assert.Equal("", agent.Version);
        Assert.False(agent.ServerEnabled);
    }
}
