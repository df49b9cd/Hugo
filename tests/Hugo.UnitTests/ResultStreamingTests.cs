using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Shouldly;

using Hugo;

using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public class ResultStreamingTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask MapStreamAsync_ShouldStopAfterFirstFailure()
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
    public async ValueTask CollectErrorsAsync_ShouldAggregateFailures()
    {
        var stream = GetResults([Result.Ok(1), Result.Fail<int>(Error.From("a")), Result.Fail<int>(Error.From("b"))]);

        var aggregated = await stream.CollectErrorsAsync(TestContext.Current.CancellationToken);

        Assert.True(aggregated.IsFailure);
        Assert.Equal(ErrorCodes.Aggregate, aggregated.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FlatMapStreamAsync_ShouldFlattenAndStopOnInnerFailure()
    {
        var source = GetValues([1, 2]);

        var flattened = Result.FlatMapStreamAsync(
            source,
            (value, ct) => value == 2
                ? FailingStream(ct)
                : SuccessfulStream(value),
            TestContext.Current.CancellationToken);

        var results = await CollectAsync(flattened);

        Assert.Equal(2, results.Count); // only first inner success and failure
        Assert.True(results[0].IsSuccess);
        Assert.Equal(10, results[0].Value);
        Assert.True(results[1].IsFailure);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FilterStreamAsync_ShouldPassThroughFailuresAndFilterSuccesses()
    {
        var stream = GetResults([Result.Ok(1), Result.Fail<int>(Error.From("boom")), Result.Ok(3)]);

        var filtered = Result.FilterStreamAsync(stream, v => v % 3 == 0, TestContext.Current.CancellationToken);
        var results = await CollectAsync(filtered);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsFailure); // failure preserved
        Assert.True(results[1].IsSuccess);
        Assert.Equal(3, results[1].Value);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ReadAllAsync_ShouldDrainChannel()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();
        await channel.Writer.WriteAsync(Result.Ok(1), TestContext.Current.CancellationToken);
        await channel.Writer.WriteAsync(Result.Ok(2), TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var list = channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], list.Select(r => r.Value));
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FanOutAsync_ShouldBroadcastResults()
    {
        var source = GetResults([Result.Ok(5), Result.Ok(6)]);
        var a = Channel.CreateUnbounded<Result<int>>();
        var b = Channel.CreateUnbounded<Result<int>>();

        await source.FanOutAsync([a.Writer, b.Writer], TestContext.Current.CancellationToken);

        var aReaderResult = await ReadAllValues(a.Reader);
        var bReaderResult = await ReadAllValues(b.Reader);
        Assert.Equal([5, 6], aReaderResult);
        Assert.Equal([5, 6], bReaderResult);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask PartitionAsync_ShouldRouteByPredicate()
    {
        var source = GetResults([Result.Ok(1), Result.Ok(2), Result.Ok(3)]);
        var even = Channel.CreateUnbounded<Result<int>>();
        var odd = Channel.CreateUnbounded<Result<int>>();

        await source.PartitionAsync(v => v % 2 == 0, even.Writer, odd.Writer, TestContext.Current.CancellationToken);

        var eveneReaderResult = await ReadAllValues(even.Reader);
        var oddReaderResult = await ReadAllValues(odd.Reader);

        Assert.Equal([2], eveneReaderResult);
        Assert.Equal([1, 3], oddReaderResult);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ForEachLinkedCancellationAsync_ShouldLinkCancellationPerItem()
    {
        var source = GetResults([Result.Ok(1), Result.Ok(2)]);
        var seen = new List<int>();

        var result = await source.ForEachLinkedCancellationAsync(async (res, ct) =>
        {
            seen.Add(res.Value);
            if (res.Value == 2)
            {
                return Result.Fail<Unit>(Error.From("stop"));
            }

            Assert.True(ct.CanBeCanceled);
            return Result.Ok(Unit.Value);
        }, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(new[] { 1, 2 }, seen);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapSuccessEachAsync_ShouldInvokeOnSuccessOnly()
    {
        var source = GetResults([Result.Ok(1), Result.Fail<int>(Error.From("boom")), Result.Ok(2)]);
        int hits = 0;

        var result = await source.TapSuccessEachAsync(async (v, _) =>
        {
            hits += v;
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(3, hits); // only successes 1 and 2
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapFailureEachAsync_ShouldInvokeOnFailureOnly()
    {
        var source = GetResults([Result.Ok(1), Result.Fail<int>(Error.From("a")), Result.Fail<int>(Error.From("b"))]);
        int count = 0;

        var result = await source.TapFailureEachAsync(async (err, _) =>
        {
            count++;
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(2, count);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ForEachAsync_ShouldReturnFailureFromCallback()
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

    private static IAsyncEnumerable<Result<int>> SuccessfulStream(int seed) =>
        GetResults([Result.Ok(seed * 10)]);

    private static async IAsyncEnumerable<Result<int>> FailingStream([EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield return Result.Fail<int>(Error.From("inner-fail", ErrorCodes.Validation));
    }

    private static async ValueTask<int[]> ReadAllValues(ChannelReader<Result<int>> reader)
    {
        var list = new List<int>();
        await foreach (var item in reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            if (item.IsSuccess)
            {
                list.Add(item.Value);
            }
        }
        return list.ToArray();
    }
}
