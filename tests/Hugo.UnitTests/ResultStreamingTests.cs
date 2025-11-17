using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Hugo;

using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public class ResultStreamingTests
{
    [Fact(Timeout = 15_000)]
    public async Task MapStreamAsync_ShouldStopAfterFirstFailure()
    {
        var source = GetValues([1, 2, 3]);

        var projected = Result.MapStreamAsync(
            source,
            (value, _) => value == 2
                ? ValueTask.FromResult(Result.Fail<int>(Error.From("boom")))
                : ValueTask.FromResult(Result.Ok(value * 10)),
            TestContext.Current.CancellationToken);

        var results = await CollectAsync(projected);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsSuccess);
        Assert.Equal(10, results[0].Value);
        Assert.True(results[1].IsFailure);
    }

    [Fact(Timeout = 15_000)]
    public async Task CollectErrorsAsync_ShouldAggregateFailures()
    {
        var stream = GetResults([Result.Ok(1), Result.Fail<int>(Error.From("a")), Result.Fail<int>(Error.From("b"))]);

        var aggregated = await stream.CollectErrorsAsync(TestContext.Current.CancellationToken);

        Assert.True(aggregated.IsFailure);
        Assert.Equal(ErrorCodes.Aggregate, aggregated.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task ForEachAsync_ShouldReturnFailureFromCallback()
    {
        var stream = GetSequence([1, 2, 3]);
        var seen = new List<int>();

        var result = await stream.ForEachAsync(async (result, ct) =>
        {
            await Task.Yield();
            if (result.IsSuccess)
            {
                seen.Add(result.Value);
            }
            return result.Value == 2 ? Result.Fail<Unit>(Error.From("stop")) : Result.Ok(Unit.Value);
        }, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(new[] { 1, 2 }, seen);
    }

    private static async IAsyncEnumerable<Result<int>> GetSequence(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            await Task.Yield();
            yield return Result.Ok(value);
        }
    }

    private static async IAsyncEnumerable<int> GetValues(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            await Task.Yield();
            yield return value;
        }
    }

    private static async IAsyncEnumerable<Result<int>> GetResults(IEnumerable<Result<int>> values)
    {
        foreach (var result in values)
        {
            await Task.Yield();
            yield return result;
        }
    }

    private static async Task<List<Result<T>>> CollectAsync<T>(IAsyncEnumerable<Result<T>> source)
    {
        var list = new List<Result<T>>();
        await foreach (var item in source.WithCancellation(TestContext.Current.CancellationToken))
        {
            list.Add(item);
        }
        return list;
    }
}
