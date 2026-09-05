namespace Snail.Toolkit.Nomad.Tests;

public sealed class HclVariablesTests
{
    [Fact]
    public void Renders_every_kind_of_value()
    {
        var rendered = HclVariables.Render(new Dictionary<string, object?>
        {
            ["name"] = "search",
            ["count"] = 2,
            ["ratio"] = 0.5,
            ["debug"] = true,
            ["nothing"] = null,
            ["nodes"] = new[] { "http://a:11434", "http://b:11434" },
            ["meta"] = new Dictionary<string, object?> { ["class"] = "code" }
        });

        Assert.Equal(
            "name = \"search\"\ncount = 2\nratio = 0.5\ndebug = true\nnothing = null\n" +
            "nodes = [\"http://a:11434\", \"http://b:11434\"]\nmeta = { \"class\" = \"code\" }\n",
            rendered);
    }

    [Fact]
    public void Escapes_quotes_backslashes_and_newlines()
    {
        Assert.Equal("\"a \\\"b\\\" \\\\ c\\n\"", HclVariables.Literal("a \"b\" \\ c\n"));
    }

    [Fact]
    public void Rejects_empty_names()
    {
        Assert.Throws<ArgumentException>(() => HclVariables.Render(new Dictionary<string, object?> { [""] = 1 }));
    }
}
