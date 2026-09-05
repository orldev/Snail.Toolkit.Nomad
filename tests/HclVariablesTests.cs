using System.Collections;

namespace Snail.Toolkit.Nomad.Tests;

public sealed class HclVariablesTests
{
    [Fact]
    public void Render_WithEveryKindOfValue_OrdersByNameAndNestsStructures()
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
            """
            count = 2
            debug = true
            meta = { "class" = "code" }
            name = "search"
            nodes = ["http://a:11434", "http://b:11434"]
            nothing = null
            ratio = 0.5

            """.ReplaceLineEndings("\n"),
            rendered);
    }

    [Fact]
    public void Render_WithTheSameVariablesInAnotherOrder_ProducesTheSameText()
    {
        var one = HclVariables.Render(new Dictionary<string, object?> { ["a"] = 1, ["b"] = 2 });
        var other = HclVariables.Render(new Dictionary<string, object?> { ["b"] = 2, ["a"] = 1 });

        Assert.Equal(one, other);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1port")]
    [InlineData("a b")]
    [InlineData("x = 1\nevil")]
    public void Render_WithANameThatIsNotAnIdentifier_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => HclVariables.Render(new Dictionary<string, object?> { [name] = 1 }));
    }

    [Fact]
    public void Literal_WithQuotesBackslashesAndNewlines_EscapesThem()
    {
        Assert.Equal("\"a \\\"b\\\" \\\\ c\\n\"", HclVariables.Literal("a \"b\" \\ c\n"));
    }

    [Theory]
    [InlineData("${var.token}", "\"$${var.token}\"")]
    [InlineData("%{if true}", "\"%%{if true}\"")]
    [InlineData("p$w%d", "\"p$w%d\"")]
    public void Literal_WithATemplateOpening_DoublesTheSign(string value, string expected)
    {
        Assert.Equal(expected, HclVariables.Literal(value));
    }

    [Fact]
    public void Literal_WithAControlCharacter_WritesAUnicodeEscape()
    {
        Assert.Equal("\"a\\u0000b\"", HclVariables.Literal("a\0b"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void Literal_WithANonFiniteNumber_Throws(double value)
    {
        Assert.Throws<ArgumentException>(() => HclVariables.Literal(value));
    }

    [Fact]
    public void Literal_WithACycle_Throws()
    {
        var cycle = new List<object>();
        cycle.Add(cycle);

        Assert.Throws<ArgumentException>(() => HclVariables.Literal(cycle));
    }

    [Fact]
    public void Literal_WithADictionaryThatOnlyReadsBack_RendersAMap()
    {
        var map = new ReadOnlyOnly(new Dictionary<string, object?> { ["port"] = 8803 });

        Assert.Equal("{ \"port\" = 8803 }", HclVariables.Literal(map));
    }

    [Fact]
    public void Literal_WithATypeThatHasNoLiteral_Throws()
    {
        Assert.Throws<ArgumentException>(() => HclVariables.Literal(new Uri("http://nomad.test:4646")));
    }

    /// <summary>A dictionary reachable only through <see cref="IReadOnlyDictionary{TKey,TValue}"/>.</summary>
    private sealed class ReadOnlyOnly(Dictionary<string, object?> inner) : IReadOnlyDictionary<string, object?>
    {
        public object? this[string key] => inner[key];

        public IEnumerable<string> Keys => inner.Keys;

        public IEnumerable<object?> Values => inner.Values;

        public int Count => inner.Count;

        public bool ContainsKey(string key) => inner.ContainsKey(key);

        public bool TryGetValue(string key, out object? value) => inner.TryGetValue(key, out value);

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() => inner.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
