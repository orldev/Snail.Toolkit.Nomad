using System.Text.Json.Nodes;

namespace Snail.Toolkit.Nomad.Tests;

public sealed class JobEditsTests
{
    private static JsonObject Job() => new()
    {
        ["ID"] = "mcp-search",
        ["TaskGroups"] = new JsonArray(
            new JsonObject { ["Name"] = "sidecar", ["Count"] = 1 },
            new JsonObject { ["Name"] = "server", ["Count"] = 1 })
    };

    /// <summary>Indexing by position edited whichever group happened to come first.</summary>
    [Fact]
    public void WithGroupCount_WithAGroupThatIsNotFirst_EditsThatGroupOnly()
    {
        var job = Job().WithGroupCount("server", 3);

        Assert.Equal(3, job.Group("server")!["Count"]!.GetValue<int>());
        Assert.Equal(1, job.Group("sidecar")!["Count"]!.GetValue<int>());
    }

    [Fact]
    public void WithGroupCount_WithAGroupTheJobDoesNotDeclare_Throws()
    {
        Assert.Throws<ArgumentException>(() => Job().WithGroupCount("worker", 2));
    }

    [Fact]
    public void WithMeta_WhenTheJobHasNoMetaBlock_CreatesIt()
    {
        var job = Job().WithMeta("deployed-by", "dashboard");

        Assert.Equal("dashboard", job["Meta"]!["deployed-by"]!.GetValue<string>());
    }

    [Fact]
    public void WithMeta_WhenTheJobAlreadyHasMeta_KeepsWhatIsThere()
    {
        var job = Job().WithMeta("mcp", "search").WithMeta("deployed-by", "dashboard");

        Assert.Equal("search", job["Meta"]!["mcp"]!.GetValue<string>());
        Assert.Equal("dashboard", job["Meta"]!["deployed-by"]!.GetValue<string>());
    }

    [Fact]
    public void Group_WithAJobThatDeclaresNoGroups_AnswersNull()
    {
        Assert.Null(new JsonObject { ["ID"] = "mcp-search" }.Group("server"));
    }

    [Fact]
    public void JobId_ReadsTheIdTheParserPutThere()
    {
        Assert.Equal("mcp-search", Job().JobId);
    }
}
