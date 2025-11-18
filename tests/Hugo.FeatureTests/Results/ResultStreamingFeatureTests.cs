using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Shouldly;
using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public sealed class ResultStreamingFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async Task MapStreamAsync_ShouldShortCircuitFeatureStreamOnFailure()
    {
        var collected = new List<int>();

        await foreach (var outcome in Result.MapStreamAsync(Values(TestContext.Current.CancellationToken), Selector, TestContext.Current.CancellationToken))
        {
            if (outcome.IsFailure)
            {
                break;
            }

            collected.Add(outcome.Value);
        }

        collected.ShouldBe([10, 20]);
    }

    [Fact(Timeout = 15_000)]
    public async Task PartitionAsync_ShouldRouteResultsAndCompleteWriters()
    {
        async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct)
        {
            yield return Result.Ok(2);
            yield return Result.Ok(3);
            yield return Result.Fail<int>(Error.From("feature-partition"));
            await Task.Delay(5, ct);
        }

        var evens = Channel.CreateUnbounded<Result<int>>();
        var odds = Channel.CreateUnbounded<Result<int>>();

        await Source(TestContext.Current.CancellationToken)
            .PartitionAsync(value => value % 2 == 0, evens.Writer, odds.Writer, TestContext.Current.CancellationToken);

        var evenValues = await evens.Reader.ReadAllAsync(TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);
        var oddValues = await odds.Reader.ReadAllAsync(TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        evenValues.ShouldHaveSingleItem().Value.ShouldBe(2);
        oddValues.Length.ShouldBe(2);
        oddValues[0].Value.ShouldBe(3);
        oddValues[1].IsFailure.ShouldBeTrue();
        evens.Reader.Completion.IsCompleted.ShouldBeTrue();
        odds.Reader.Completion.IsCompleted.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOutAsync_ShouldBroadcastResultsToAllDestinations()
    {
        async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; i < 3; i++)
            {
                ct.ThrowIfCancellationRequested();
                yield return Result.Ok(i);
                await Task.Yield();
            }
        }

        var left = Channel.CreateUnbounded<Result<int>>();
        var right = Channel.CreateUnbounded<Result<int>>();

        await Source(TestContext.Current.CancellationToken)
            .FanOutAsync([left.Writer, right.Writer], TestContext.Current.CancellationToken);

        var leftValues = await left.Reader.ReadAllAsync(TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);
        var rightValues = await right.Reader.ReadAllAsync(TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        leftValues.Select(r => r.Value).ShouldBe([0, 1, 2]);
        rightValues.Select(r => r.Value).ShouldBe([0, 1, 2]);
        left.Reader.Completion.IsCompleted.ShouldBeTrue();
        right.Reader.Completion.IsCompleted.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task WindowAsync_ShouldYieldTrailingWindowAndSurfaceFailure()
    {
        async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct)
        {
            yield return Result.Ok(1);
            yield return Result.Ok(2);
            yield return Result.Fail<int>(Error.From("feature-window-fail"));
            yield return Result.Ok(3);
            await Task.Delay(5, ct);
        }

        var windows = new List<Result<IReadOnlyList<int>>>();
        await foreach (var outcome in Source(TestContext.Current.CancellationToken).WindowAsync(2, TestContext.Current.CancellationToken))
        {
            windows.Add(outcome);
        }

        windows.Count.ShouldBe(3);
        windows[0].IsSuccess.ShouldBeTrue();
        windows[0].Value.ShouldBe([1, 2]);
        windows[1].IsFailure.ShouldBeTrue();
        windows[2].IsSuccess.ShouldBeTrue();
        windows[2].Value.ShouldBe([3]);
    }

    [Fact(Timeout = 15_000)]
    public async Task ForEachLinkedCancellationAsync_ShouldIterateWithLinkedTokens()
    {
        async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct)
        {
            yield return Result.Ok(42);
            await Task.Yield();
            yield return Result.Ok(84);
        }

        var observed = new List<CancellationToken>();

        var outcome = await Source(TestContext.Current.CancellationToken).ForEachLinkedCancellationAsync(
            (result, linkedToken) =>
            {
                observed.Add(linkedToken);
                return ValueTask.FromResult(Result.Ok(Unit.Value));
            },
            TestContext.Current.CancellationToken);

        outcome.IsSuccess.ShouldBeTrue();
        observed.Count.ShouldBe(2);
        observed[0].ShouldNotBe(observed[1]);
        observed.TrueForAll(token => token.CanBeCanceled).ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_ShouldSurfaceSourceExceptions()
    {
        async IAsyncEnumerable<Result<int>> Faulty([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("fan-in feature failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }

        async IAsyncEnumerable<Result<int>> Healthy([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Result.Ok(1);
            await Task.Yield();
            yield return Result.Ok(2);
        }

        var writer = Channel.CreateUnbounded<Result<int>>();

        var fanInResult = await Result.FanInAsync(
            new[] { Faulty(TestContext.Current.CancellationToken), Healthy(TestContext.Current.CancellationToken) },
            writer.Writer,
            TestContext.Current.CancellationToken);

        fanInResult.IsFailure.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_ShouldPropagateCancellationAndCloseWriter()
    {
        async IAsyncEnumerable<Result<int>> Slow([EnumeratorCancellation] CancellationToken ct = default)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                yield return Result.Ok(99);
                await Task.Delay(50, ct);
            }
        }

        using var cts = new CancellationTokenSource();
        var writer = Channel.CreateUnbounded<Result<int>>();

        var task = Result.FanInAsync(new[] { Slow(cts.Token) }, writer.Writer, cts.Token);
        cts.Cancel();

        var outcome = await task;

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        await Should.ThrowAsync<OperationCanceledException>(async () => await writer.Reader.Completion);
    }

    private static async IAsyncEnumerable<int> Values([EnumeratorCancellation] CancellationToken token)
    {
        yield return 1;
        await Task.Yield();
        yield return 2;
        await Task.Yield();
        yield return 3;
        await Task.Delay(5, token);
        yield return 4;
    }

    private static ValueTask<Result<int>> Selector(int value, CancellationToken token)
    {
        if (value == 3)
        {
            return ValueTask.FromResult(Result.Fail<int>(Error.From("feature-fail")));
        }

        return ValueTask.FromResult(Result.Ok(value * 10));
    }
}
