using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;


using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public sealed class ResultStreamingIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask MapStreamAsync_ShouldProduceMappedValuesUntilFailure()
    {
        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            yield return 2;
            yield return 3;
            await Task.Yield();
        }

        var projected = Result.MapStreamAsync<int, int>(
            Source(TestContext.Current.CancellationToken),
            (value, _) => value < 3
                ? ValueTask.FromResult(Result.Ok(value * 2))
                : ValueTask.FromResult(Result.Fail<int>(Error.From("stop"))),
            TestContext.Current.CancellationToken);

        var results = await CollectAsync(projected, TestContext.Current.CancellationToken);

        results.Count.ShouldBe(3);
        results[0].Value.ShouldBe(2);
        results[1].Value.ShouldBe(4);
        results[2].IsFailure.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapStreamAsync_ShouldTranslateSelectorExceptions()
    {
        var source = ToValues([1], TestContext.Current.CancellationToken);

        var projected = Result.MapStreamAsync<int, int>(
            source,
            (_, _) => throw new InvalidOperationException("boom"),
            TestContext.Current.CancellationToken);

        var results = await CollectAsync(projected, TestContext.Current.CancellationToken);

        results.ShouldHaveSingleItem().Error?.Code.ShouldBe(ErrorCodes.Exception);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapStreamAsync_ShouldReturnCanceledWhenEnumeratorThrows()
    {
        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new OperationCanceledException(ct);
#pragma warning disable CS0162
            yield return 0;
#pragma warning restore CS0162
        }

        var results = await CollectAsync(
            Result.MapStreamAsync(Source(TestContext.Current.CancellationToken), (value, _) => ValueTask.FromResult(Result.Ok(value)), TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        results.ShouldHaveSingleItem().Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FlatMapStreamAsync_ShouldSurfaceInnerCancellation()
    {
        async IAsyncEnumerable<int> Outer([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return 1;
            await Task.Yield();
        }

        async IAsyncEnumerable<Result<int>> Canceling([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Delay(50, ct);
            yield return Result.Ok(10);
        }

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(5);

        var flattened = Result.FlatMapStreamAsync(
            Outer(cts.Token),
            (_, token) => Canceling(token),
            cts.Token);

        var results = await CollectAsync(flattened, TestContext.Current.CancellationToken);

        results.ShouldHaveSingleItem().Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FlatMapStreamAsync_ShouldFailWhenSelectorReturnsNull()
    {
        var stream = Result.FlatMapStreamAsync<int, int>(
            ToValues([1], TestContext.Current.CancellationToken),
            (_, _) => null!,
            TestContext.Current.CancellationToken);

        var results = await CollectAsync(stream, TestContext.Current.CancellationToken);

        results.ShouldHaveSingleItem().Error?.Message.ShouldContain("Selector returned null stream.");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FilterStreamAsync_ShouldDropNonMatchingValuesButKeepFailures()
    {
        async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct)
        {
            yield return Result.Ok(1);
            yield return Result.Fail<int>(Error.From("fail"));
            yield return Result.Ok(2);
        }

        var filtered = Result.FilterStreamAsync(Source(TestContext.Current.CancellationToken), v => v % 2 == 0, TestContext.Current.CancellationToken);
        var results = await CollectAsync(filtered, TestContext.Current.CancellationToken);

        results.Count.ShouldBe(2);
        results[0].IsFailure.ShouldBeTrue();
        results[1].Value.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CollectErrorsAsync_ShouldAggregateFailures()
    {
        async IAsyncEnumerable<Result<int>> Stream([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Result.Ok(1);
            yield return Result.Fail<int>(Error.From("first"));
            await Task.Delay(10, ct);
            yield return Result.Fail<int>(Error.From("second"));
        }

        var outcome = await Stream(TestContext.Current.CancellationToken).CollectErrorsAsync(TestContext.Current.CancellationToken);

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error?.Code.ShouldBe(ErrorCodes.Aggregate);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ReadAllAsync_ShouldDrainChannelAfterFanIn()
    {
        var first = ToResults([Result.Ok(1), Result.Ok(2)], TestContext.Current.CancellationToken);
        var second = ToResults([Result.Ok(3)], TestContext.Current.CancellationToken);
        var channel = Channel.CreateUnbounded<Result<int>>();

        var fanInResult = await Result.FanInAsync(new[] { first, second }, channel.Writer, TestContext.Current.CancellationToken);
        fanInResult.IsSuccess.ShouldBeTrue();

        var drained = await channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        drained.Length.ShouldBe(3);
        drained.Select(r => r.Value).OrderBy(v => v).ToArray().ShouldBe(new[] { 1, 2, 3 });
    }

    private static async IAsyncEnumerable<Result<int>> ToResults(IEnumerable<Result<int>> results, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var result in results)
        {
            ct.ThrowIfCancellationRequested();
            yield return result;
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<int> ToValues(IEnumerable<int> values, [EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var value in values)
        {
            ct.ThrowIfCancellationRequested();
            yield return value;
            await Task.Yield();
        }
    }

    private static async Task<List<Result<T>>> CollectAsync<T>(IAsyncEnumerable<Result<T>> stream, CancellationToken ct)
    {
        var list = new List<Result<T>>();
        await foreach (var result in stream.WithCancellation(ct))
        {
            list.Add(result);
        }

        return list;
    }
}
