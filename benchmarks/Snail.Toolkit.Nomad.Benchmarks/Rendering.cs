using BenchmarkDotNet.Attributes;

namespace Snail.Toolkit.Nomad.Benchmarks;

/// <summary>Fifteen job variables rendered as the HCL var-file a job submission carries.</summary>
[MemoryDiagnoser]
public class Rendering
{
    private readonly Dictionary<string, object?> _variables = Answers.Variables();

    [Benchmark]
    public string Render() => HclVariables.Render(_variables);
}
