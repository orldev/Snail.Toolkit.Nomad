using BenchmarkDotNet.Attributes;

namespace Snail.Toolkit.Nomad.Benchmarks;

/// <summary>A list endpoint read into its models, and the tail of a task's log read into a string.</summary>
/// <remarks>Five hundred jobs is some 500 KB in one buffered answer; a tail of 4 KB is what a dashboard asks for.</remarks>
[MemoryDiagnoser]
public class Reading
{
    private NomadClient _jobs = null!;

    private NomadClient _logs = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _jobs = Answers.Client(Answers.Jobs(500), "application/json");
        _logs = Answers.Client(Answers.Log(4096), "text/plain");

        var tail = await _logs.ReadLogsAsync("a1b2c3", "server", tailBytes: 4096).ConfigureAwait(false);

        if (tail != System.Text.Encoding.UTF8.GetString(Answers.Log(4096)))
            throw new InvalidOperationException("The log tail is not the text that was sent.");
    }

    [Benchmark]
    public async Task<int> ListJobs() => (await _jobs.ListJobsAsync()).Count;

    [Benchmark]
    public async Task<int> ReadLogTail() => (await _logs.ReadLogsAsync("a1b2c3", "server", tailBytes: 4096)).Length;
}
