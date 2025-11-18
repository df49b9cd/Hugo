using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Shouldly;

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

        results.Count.ShouldBe(2);
        results[0].IsSuccess.ShouldBeTrue();
        results[0].Value.ShouldBe(10);
        results[1].IsFailure.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapStreamAsync_ShouldSurfaceCancellationFromSource()
    {
        async IAsyncEnumerable<int> Canceling([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return 1;
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var projected = Result.MapStreamAsync(
            Canceling(cts.Token),
            (value, _) => ValueTask.FromResult(Result.Ok(value)),
            cts.Token);

        var results = await CollectAsync(projected);

        results.Count.ShouldBe(1);
        results[0].IsFailure.ShouldBeTrue();
        results[0].Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapStreamAsync_ShouldSurfaceEnumerationException()
    {
        var source = new ThrowingAsyncEnumerable<int>(new InvalidOperationException("stream blow-up"));

        var projected = Result.MapStreamAsync<int, int>(
            source,
            (value, _) => ValueTask.FromResult(Result.Ok(value)),
            TestContext.Current.CancellationToken);

        var results = await CollectAsync(projected);

        results.ShouldHaveSingleItem().Error?.Code.ShouldBe(ErrorCodes.Exception);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapStreamAsync_ShouldReturnCanceledWhenSelectorThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var projected = Result.MapStreamAsync<int, int>(
            GetValues([1]),
            (_, token) => throw new OperationCanceledException(token),
            cts.Token);

        var results = await CollectAsync(projected);

        results.ShouldHaveSingleItem().Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CollectErrorsAsync_ShouldAggregateFailures()
    {
        var stream = GetResults([Result.Ok(1), Result.Fail<int>(Error.From("a")), Result.Fail<int>(Error.From("b"))]);

        var aggregated = await stream.CollectErrorsAsync(TestContext.Current.CancellationToken);

        aggregated.IsFailure.ShouldBeTrue();
        aggregated.Error?.Code.ShouldBe(ErrorCodes.Aggregate);
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

        results.Count.ShouldBe(2); // only first inner success and failure
        results[0].IsSuccess.ShouldBeTrue();
        results[0].Value.ShouldBe(10);
        results[1].IsFailure.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FlatMapStreamAsync_ShouldFailWhenSelectorReturnsNullStream()
    {
        var source = GetValues([1]);

        var flattened = Result.FlatMapStreamAsync<int, int>(
            source,
            (_, _) => (IAsyncEnumerable<Result<int>>?)null!,
            TestContext.Current.CancellationToken);

        var results = await CollectAsync(flattened);

        results.Count.ShouldBe(1);
        results[0].IsFailure.ShouldBeTrue();
        results[0].Error?.Message.ShouldContain("null stream");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FilterStreamAsync_ShouldPassThroughFailuresAndFilterSuccesses()
    {
        var stream = GetResults([Result.Ok(1), Result.Fail<int>(Error.From("boom")), Result.Ok(3)]);

        var filtered = Result.FilterStreamAsync(stream, v => v % 3 == 0, TestContext.Current.CancellationToken);
        var results = await CollectAsync(filtered);

        results.Count.ShouldBe(2);
        results[0].IsFailure.ShouldBeTrue(); // failure preserved
        results[1].IsSuccess.ShouldBeTrue();
        results[1].Value.ShouldBe(3);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ReadAllAsync_ShouldDrainChannel()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();
        await channel.Writer.WriteAsync(Result.Ok(1), TestContext.Current.CancellationToken);
        await channel.Writer.WriteAsync(Result.Ok(2), TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var list = channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken);

        var values = await list.Select(r => r.Value).ToArrayAsync(TestContext.Current.CancellationToken);
        values.ShouldBe(new[] { 1, 2 });
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
        aReaderResult.ShouldBe([5, 6]);
        bReaderResult.ShouldBe([5, 6]);
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

        eveneReaderResult.ShouldBe([2]);
        oddReaderResult.ShouldBe([1, 3]);
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

            ct.CanBeCanceled.ShouldBeTrue();
            return Result.Ok(Unit.Value);
        }, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        seen.ShouldBe(new[] { 1, 2 });
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

        result.IsFailure.ShouldBeTrue();
        hits.ShouldBe(3); // only successes 1 and 2
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

        result.IsFailure.ShouldBeTrue();
        count.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapSuccessEachAggregateErrorsAsync_ShouldAggregateFailures()
    {
        var source = GetResults([Result.Ok(1), Result.Fail<int>(Error.From("a")), Result.Fail<int>(Error.From("b")), Result.Ok(2)]);
        int sum = 0;

        var result = await source.TapSuccessEachAggregateErrorsAsync(async (v, _) =>
        {
            sum += v;
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Aggregate);
        sum.ShouldBe(3); // only successes 1 and 2
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapFailureEachAggregateErrorsAsync_ShouldAggregateFailures()
    {
        var source = GetResults([Result.Ok(1), Result.Fail<int>(Error.From("a")), Result.Fail<int>(Error.From("b"))]);
        int count = 0;

        var result = await source.TapFailureEachAggregateErrorsAsync(async (_, _) =>
        {
            count++;
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Aggregate);
        count.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ForEachLinkedCancellationAsync_ShouldUseLinkedToken()
    {
        var source = GetResults([Result.Ok(1), Result.Ok(2)]);
        int cancellations = 0;

        var result = await source.ForEachLinkedCancellationAsync(async (_, token) =>
        {
            token.Register(() => Interlocked.Increment(ref cancellations));
            await Task.Yield();
            return Result.Ok(Unit.Value);
        }, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        cancellations.ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapFailureEachIgnoreErrorsAsync_ShouldReturnSuccess_WhenFailuresIgnored()
    {
        var source = GetResults([Result.Fail<int>(Error.From("ignored")), Result.Ok(1)]);
        int hits = 0;

        var result = await source.TapFailureEachIgnoreErrorsAsync(async (_, _) =>
        {
            Interlocked.Increment(ref hits);
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        hits.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapSuccessEachIgnoreErrorsAsync_ShouldReturnSuccess()
    {
        var source = GetResults([Result.Ok(1), Result.Fail<int>(Error.From("a")), Result.Ok(2)]);
        int sum = 0;

        var result = await source.TapSuccessEachIgnoreErrorsAsync(async (v, _) =>
        {
            sum += v;
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        sum.ShouldBe(3);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapFailureEachIgnoreErrorsAsync_ShouldReturnSuccess()
    {
        var source = GetResults([Result.Ok(1), Result.Fail<int>(Error.From("a")), Result.Fail<int>(Error.From("b"))]);
        int count = 0;

        var result = await source.TapFailureEachIgnoreErrorsAsync(async (_, _) =>
        {
            count++;
            await Task.Yield();
        }, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        count.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapStreamAsync_ShouldTranslateSelectorException()
    {
        var source = GetValues([1]);

        var projected = Result.MapStreamAsync<int, int>(
            source,
            (_, _) => throw new InvalidOperationException("boom"),
            TestContext.Current.CancellationToken);

        var results = await CollectAsync(projected);

        results.Count.ShouldBe(1);
        results[0].IsFailure.ShouldBeTrue();
        results[0].Error?.Code.ShouldBe(ErrorCodes.Exception);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapStreamAsync_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var source = GetValues([1]);
        var projected = Result.MapStreamAsync<int, int>(source, (v, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Result.Ok(v));
        }, cts.Token);

        var results = await CollectAsync(projected);

        results.ShouldHaveSingleItem().Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapStreamAsync_ShouldTranslateMoveNextFailure()
    {
        async IAsyncEnumerable<int> Failing([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return await Task.FromException<int>(new InvalidOperationException("enumerator boom"));
        }

        var projected = Result.MapStreamAsync(
            Failing(TestContext.Current.CancellationToken),
            (value, _) => ValueTask.FromResult(Result.Ok(value)),
            TestContext.Current.CancellationToken);

        var results = await CollectAsync(projected);

        results.ShouldHaveSingleItem().Error?.Code.ShouldBe(ErrorCodes.Exception);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ToChannelAsync_ShouldCompleteWriter()
    {
        var source = GetResults([Result.Ok(1), Result.Ok(2)]);
        var channel = Channel.CreateUnbounded<Result<int>>();

        await source.ToChannelAsync(channel.Writer, TestContext.Current.CancellationToken);

        var collected = await channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken).Select(r => r.Value).ToArrayAsync(TestContext.Current.CancellationToken);
        collected.ShouldBe(new[] { 1, 2 });
        (await channel.Reader.WaitToReadAsync(TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FanInAsync_ShouldCancelSources()
    {
        using var cts = new CancellationTokenSource();
        var slow = SlowResults(cts.Token);
        var channel = Channel.CreateUnbounded<Result<int>>();

        var fanInTask = Result.FanInAsync(new[] { slow }, channel.Writer, cts.Token);
        cts.Cancel();

        var result = await fanInTask;
        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MapStreamAsync_ShouldHandleMoveNextException()
    {
        async IAsyncEnumerable<int> Throwing([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return await Task.FromException<int>(new InvalidOperationException("move-next exploded"));
        }

        var projected = Result.MapStreamAsync(
            Throwing(TestContext.Current.CancellationToken),
            (value, _) => ValueTask.FromResult(Result.Ok(value)),
            TestContext.Current.CancellationToken);

        var results = await CollectAsync(projected);

        results.ShouldHaveSingleItem().Error?.Code.ShouldBe(ErrorCodes.Exception);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FlatMapStreamAsync_ShouldSurfaceInnerMoveNextException()
    {
        async IAsyncEnumerable<Result<int>> ExplodingInner([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return await Task.FromException<Result<int>>(new InvalidOperationException("inner boom"));
        }

        var flattened = Result.FlatMapStreamAsync(
            GetValues([1]),
            (_, _) => ExplodingInner(TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        var results = await CollectAsync(flattened);

        results.ShouldHaveSingleItem().Error?.Code.ShouldBe(ErrorCodes.Exception);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FlatMapStreamAsync_ShouldReturnCanceledWhenOuterEnumerationStops()
    {
        async IAsyncEnumerable<int> CancelingOuter([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            yield return await Task.FromCanceled<int>(ct);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var flattened = Result.FlatMapStreamAsync(
            CancelingOuter(cts.Token),
            (_, _) => SuccessfulStream(1),
            cts.Token);

        var results = await CollectAsync(flattened);

        results.ShouldHaveSingleItem().Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FanInAsync_ShouldCompleteWriterWithFailure()
    {
#pragma warning disable CS0162
        async IAsyncEnumerable<Result<int>> Throwing([EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            throw new InvalidOperationException("fan-in failure");
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
#pragma warning restore CS0162

        var channel = Channel.CreateUnbounded<Result<int>>();

        var result = await Result.FanInAsync(new[] { Throwing(TestContext.Current.CancellationToken) }, channel.Writer, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Cause.ShouldBeOfType<InvalidOperationException>();

        await Should.ThrowAsync<ChannelClosedException>(async () => await channel.Writer.WriteAsync(Result.Ok(99), TestContext.Current.CancellationToken));
        channel.Reader.Completion.IsFaulted.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ToChannelAsync_ShouldWriteCancellationSentinelWhenWriterClosed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        async IAsyncEnumerable<Result<int>> Canceling([EnumeratorCancellation] CancellationToken token)
        {
            await Task.Delay(Timeout.Infinite, token);
            yield break;
        }

        var channel = Channel.CreateUnbounded<Result<int>>();
        channel.Writer.TryComplete(); // force TryWrite to fail

        await Canceling(cts.Token).ToChannelAsync(channel.Writer, cts.Token);

        channel.Reader.Completion.IsCompleted.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ToChannelAsync_ShouldFallbackToWriteAsyncWhenBufferIsFull()
    {
        var channel = Channel.CreateBounded<Result<int>>(1);
        await channel.Writer.WriteAsync(Result.Ok(7), TestContext.Current.CancellationToken);

        async IAsyncEnumerable<Result<int>> Canceling([EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            throw new OperationCanceledException(ct);
#pragma warning disable CS0162
            yield return Result.Ok(0);
#pragma warning restore CS0162
        }

        var forwarding = Result.ToChannelAsync(Canceling(TestContext.Current.CancellationToken), channel.Writer, TestContext.Current.CancellationToken);

        var buffered = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        buffered.Value.ShouldBe(7);

        await forwarding;

        var sentinel = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        sentinel.IsFailure.ShouldBeTrue();
        sentinel.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        channel.Reader.Completion.IsCompleted.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FlatMapStreamAsync_ShouldReturnFailureWhenSelectorReturnsNull()
    {
        var source = GetValues([1]);

        var stream = Result.FlatMapStreamAsync<int, int>(source, (_, _) => null!, TestContext.Current.CancellationToken);

        var collected = await CollectAsync(stream);

        collected.ShouldHaveSingleItem();
        collected[0].IsFailure.ShouldBeTrue();
        collected[0].Error?.Message.ShouldBe("Selector returned null stream.");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WindowAsync_ShouldFlushAndResetAfterFailure()
    {
        var source = GetResults([
            Result.Ok(1),
            Result.Ok(2),
            Result.Fail<int>(Error.From("boom")),
            Result.Ok(3),
            Result.Ok(4)
        ]);

        var windows = await source.WindowAsync(2, TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        windows.Length.ShouldBe(3);
        windows[0].IsSuccess.ShouldBeTrue();
        windows[0].Value.ShouldBe([1, 2]);
        windows[1].IsFailure.ShouldBeTrue(); // failure propagated, buffer cleared
        windows[2].IsSuccess.ShouldBeTrue();
        windows[2].Value.ShouldBe([3, 4]);
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

        result.IsFailure.ShouldBeTrue();
        seen.ShouldBe(new[] { 1, 2 });
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ForEachLinkedCancellationAsync_ShouldCreateFreshLinkedTokenPerItem()
    {
        var stream = GetSequence([10, 11]);
        var observedTokens = new List<CancellationToken>();

        var result = await stream.ForEachLinkedCancellationAsync((value, linkedToken) =>
        {
            observedTokens.Add(linkedToken);
            linkedToken.CanBeCanceled.ShouldBeTrue();
            return ValueTask.FromResult(Result.Ok(Unit.Value));
        }, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        observedTokens.Count.ShouldBe(2);
        observedTokens[0].ShouldNotBe(observedTokens[1]);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ReadAllAsync_ShouldYieldUntilWriterCompletes()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();

        await channel.Writer.WriteAsync(Result.Ok(5), TestContext.Current.CancellationToken);
        await channel.Writer.WriteAsync(Result.Fail<int>(Error.From("channel-failure")), TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var observed = await channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);

        observed.Length.ShouldBe(2);
        observed[0].Value.ShouldBe(5);
        observed[1].IsFailure.ShouldBeTrue();
        channel.Reader.Completion.IsCompleted.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ToChannelAsync_ShouldEmitCanceledSentinelWhenCanceled()
    {
        using var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<Result<int>>();

        async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct)
        {
            var i = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                yield return Result.Ok(++i);
                await Task.Delay(5, ct);
            }
        }

        var forwardTask = Result.ToChannelAsync(Source(cts.Token), channel.Writer, cts.Token);
        cts.CancelAfter(15);

        await forwardTask;

        var results = await channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken).ToArrayAsync(TestContext.Current.CancellationToken);
        results.Last().Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TapFailureEachAggregateErrorsAsync_ShouldAggregateAllFailures()
    {
        var failures = new[]
        {
            Result.Fail<int>(Error.From("first", ErrorCodes.Validation)),
            Result.Fail<int>(Error.From("second", ErrorCodes.Validation))
        };

        var tapped = new List<string>();
        var outcome = await failures.ToAsyncEnumerable()
            .TapFailureEachAggregateErrorsAsync((error, _) =>
            {
                tapped.Add(error.Message);
                return ValueTask.CompletedTask;
            }, TestContext.Current.CancellationToken);

        tapped.ShouldBe(new[] { "first", "second" });
        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Message.ShouldContain("One or more failures occurred while processing the stream.");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CollectErrorsAsync_ShouldReturnAggregateWhenFailuresPresent()
    {
        var stream = GetResults([
            Result.Ok(1),
            Result.Fail<int>(Error.From("first", ErrorCodes.Validation)),
            Result.Fail<int>(Error.From("second", ErrorCodes.Validation))
        ]);

        var outcome = await stream.CollectErrorsAsync(TestContext.Current.CancellationToken);

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error!.Message.ShouldContain("One or more failures occurred while processing the stream.");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ToChannelAsync_ShouldPublishCanceledSentinelAndCompleteWriter()
    {
        async IAsyncEnumerable<Result<int>> Canceling([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return Result.Ok(0);
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var channel = Channel.CreateUnbounded<Result<int>>();

        await Result.ToChannelAsync(Canceling(cts.Token), channel.Writer, cts.Token);

        var sentinel = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);

        sentinel.IsFailure.ShouldBeTrue();
        sentinel.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        channel.Reader.Completion.IsCompleted.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FanInAsync_ShouldReturnCanceledAndCompleteWriterWhenTokenCanceled()
    {
        async IAsyncEnumerable<Result<int>> Slow([EnumeratorCancellation] CancellationToken ct)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                yield return Result.Ok(1);
                await Task.Delay(25, ct);
            }
        }

        using var cts = new CancellationTokenSource();
        var writer = Channel.CreateUnbounded<Result<int>>();

        var fanInTask = Result.FanInAsync(new[] { Slow(cts.Token) }, writer.Writer, cts.Token);
        cts.Cancel();

        var outcome = await fanInTask;

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        await Should.ThrowAsync<OperationCanceledException>(async () => await writer.Reader.Completion);
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

    private static async IAsyncEnumerable<Result<int>> SlowResults([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(5, ct);
            yield return Result.Ok(1);
        }
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
        return [.. list];
    }

    private sealed class ThrowingAsyncEnumerable<T> : IAsyncEnumerable<T>, IAsyncEnumerator<T>
    {
        private readonly Exception _exception;

        public ThrowingAsyncEnumerable(Exception exception) => _exception = exception;

        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public T Current => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public ValueTask<bool> MoveNextAsync() => ValueTask.FromException<bool>(_exception);
    }
}
