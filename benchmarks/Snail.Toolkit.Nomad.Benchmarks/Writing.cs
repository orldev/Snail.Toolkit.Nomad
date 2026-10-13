using System.Text.Json.Nodes;
using BenchmarkDotNet.Attributes;

namespace Snail.Toolkit.Nomad.Benchmarks;

/// <summary>A parsed job registered: the job copied into the request, the body written, the evaluation read.</summary>
/// <remarks>Eight task groups is some 2.6 KB of JSON, a service of a few parts.</remarks>
[MemoryDiagnoser]
public class Writing
{
    private readonly JsonObject _job = Answers.Job(8);

    private NomadClient _registry = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _registry = Answers.Client(Answers.Registration(), "application/json");

        var registration = await _registry.RegisterJobAsync(_job).ConfigureAwait(false);

        if (registration is not { EvalId: "3c5f1a2b-0d4e-4f6a-8b9c-1d2e3f4a5b6c", JobModifyIndex: 42 })
            throw new InvalidOperationException("The registration is not the one that was answered.");
    }

    [Benchmark]
    public async Task<string> RegisterJob() => (await _registry.RegisterJobAsync(_job)).EvalId;
}
