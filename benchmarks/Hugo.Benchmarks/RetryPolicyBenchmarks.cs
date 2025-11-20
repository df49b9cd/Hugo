using BenchmarkDotNet.Attributes;

using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Results)]
public class RetryPolicyBenchmarks
{
    [Params(3)]
    public int MaxAttempts { get; set; }

    [Benchmark(Baseline = true)]
    public static Task<Result<int>> DirectSuccessAsync() => Task.FromResult(Result.Ok(1));

    [Benchmark]
    public Task<Result<int>> Retry_SucceedsOnLastAttemptAsync()
    {
        int attempts = 0;

        return ResultPipeline.RetryAsync(
            (context, token) =>
            {
                int current = Interlocked.Increment(ref attempts);
                if (current < MaxAttempts)
                {
                    return ValueTask.FromResult(Result.Fail<int>(Error.From("transient", "error.retry")));
                }

                return ValueTask.FromResult(Result.Ok(current));
            },
            maxAttempts: MaxAttempts,
            cancellationToken: CancellationToken.None).AsTask();
    }

    [Benchmark]
    public Task<Result<int>> Retry_AlwaysFailsAsync() =>
        ResultPipeline.RetryAsync(
            (_, _) => ValueTask.FromResult(Result.Fail<int>(Error.From("boom", "error.retry"))),
            maxAttempts: MaxAttempts,
            cancellationToken: CancellationToken.None).AsTask();
}
