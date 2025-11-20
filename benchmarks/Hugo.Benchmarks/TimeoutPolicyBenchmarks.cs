using BenchmarkDotNet.Attributes;
using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Results)]
public class TimeoutPolicyBenchmarks
{
    [Params(64, 256)]
    public int OperationCount { get; set; }

    [Params(1, 4)] // milliseconds per step
    public int StepDelayMs { get; set; }

    [Benchmark(Baseline = true)]
    public async Task<Result<int>> DirectExecutionAsync()
    {
        int total = 0;
        for (var i = 0; i < OperationCount; i++)
        {
            await Task.Delay(StepDelayMs).ConfigureAwait(false);
            total++;
        }

        return Result.Ok(total);
    }

    [Benchmark]
    public async Task<Result<int>> WithTimeoutAsync()
    {
        int total = 0;
        var timeout = TimeSpan.FromMilliseconds(StepDelayMs * 2);

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
            await Task.Delay(StepDelayMs, token).ConfigureAwait(false);
            return Result.Ok(1);
        }
    }
}
