using BenchmarkDotNet.Attributes;
using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Results)]
public class TimeoutPolicyBenchmarks
{
    [Params(64, 256)]
    public int OperationCount { get; set; }

    [Params(200, 800)] // microseconds of spin to avoid scheduler noise
    public int StepWorkMicros { get; set; }

    [Benchmark(Baseline = true)]
    public async Task<Result<int>> DirectExecutionAsync()
    {
        int total = 0;
        for (var i = 0; i < OperationCount; i++)
        {
            BenchmarkWorkloads.SimulateLightCpuWork();
            total++;
        }

        return Result.Ok(total);
    }

    [Benchmark]
    public async Task<Result<int>> WithTimeoutAsync()
    {
        int total = 0;
        var timeout = TimeSpan.FromMilliseconds(1);

        for (var i = 0; i < OperationCount; i++)
        {
            var result = await ResultPipeline.WithTimeoutAsync(
                (_, token) => DoWorkAsync(token),
                timeout,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            if (result.IsSuccess)
            {
                total++;
            }
        }

        return Result.Ok(total);

        async ValueTask<Result<int>> DoWorkAsync(CancellationToken token)
        {
            // use CPU spin to reduce scheduler timing noise
            var iterations = StepWorkMicros;
            for (var i = 0; i < iterations; i++)
            {
                token.ThrowIfCancellationRequested();
                Thread.SpinWait(16);
            }
            return Result.Ok(1);
        }
    }
}
