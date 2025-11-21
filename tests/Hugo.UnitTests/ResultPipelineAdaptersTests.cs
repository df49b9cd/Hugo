using System.Threading.Channels;

using Hugo.Policies;

using Microsoft.Extensions.Time.Testing;


using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public class ResultPipelineAdaptersTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask SelectAsync_ShouldAbsorbCompensation()
    {
        var channel = Channel.CreateUnbounded<int>();
        var testToken = TestContext.Current.CancellationToken;

        await channel.Writer.WriteAsync(1, testToken);
        channel.Writer.TryComplete();

        var (context, scope) = CreateContext("select-test");
        int compensationInvocations = 0;

        var cases = new[]
        {
            ChannelCase.Create(channel.Reader, (value, ct) =>
                ValueTask.FromResult(
                    Result.Ok(value)
                        .WithCompensation(_ =>
                        {
                            Interlocked.Increment(ref compensationInvocations);
                            return ValueTask.CompletedTask;
                        })))
        };

        var result = await ResultPipelineChannels.SelectAsync(context, cases, cancellationToken: testToken);

        result.IsSuccess.ShouldBeTrue();
        scope.HasActions.ShouldBeTrue();

        var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
        await Result.RunCompensationAsync(policy, scope, testToken);
        compensationInvocations.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FanInAsync_ShouldTrackCompensationPerValue()
    {
        var channelA = Channel.CreateUnbounded<int>();
        var channelB = Channel.CreateUnbounded<int>();

        var testToken = TestContext.Current.CancellationToken;

        await channelA.Writer.WriteAsync(1, testToken);
        await channelB.Writer.WriteAsync(2, testToken);
        channelA.Writer.TryComplete();
        channelB.Writer.TryComplete();

        var (context, scope) = CreateContext("fanin-test");
        int compensationCount = 0;

        Func<int, CancellationToken, ValueTask<Result<Unit>>> handler = async (value, ct) =>
        {
            await Task.Yield();
            return Result.Ok(Unit.Value).WithCompensation(_ =>
            {
                Interlocked.Increment(ref compensationCount);
                return ValueTask.CompletedTask;
            });
        };

        var fanInResult = await ResultPipelineChannels.FanInAsync(
            context,
            [channelA.Reader, channelB.Reader],
            handler,
            cancellationToken: testToken);

        fanInResult.IsSuccess.ShouldBeTrue();
        scope.HasActions.ShouldBeTrue();

        var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
        await Result.RunCompensationAsync(policy, scope, testToken);
        compensationCount.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RetryAsync_ShouldRetryUntilSuccess()
    {
        int attempts = 0;
        var testToken = TestContext.Current.CancellationToken;

        var result = await ResultPipeline.RetryAsync(
            async (_, _) =>
            {
                int current = Interlocked.Increment(ref attempts);
                if (current < 3)
                {
                    return Result.Fail<int>(Error.From("retry"));
                }

                return Result.Ok(42);
            },
            maxAttempts: 3,
            cancellationToken: testToken);

        result.IsSuccess.ShouldBeTrue();
        (result.Value + 1).ShouldBe(43);
        attempts.ShouldBe(3);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WithTimeoutAsync_ShouldReturnTimeoutError()
    {
        var result = await ResultPipeline.WithTimeoutAsync(
            async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
                return Result.Ok(1);
            },
            timeout: TimeSpan.FromMilliseconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Timeout);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask SelectBuilder_ShouldComposeCases()
    {
        var (context, scope) = CreateContext("builder");
        var channel = Channel.CreateUnbounded<int>();
        await channel.Writer.WriteAsync(7, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var builder = ResultPipelineChannels.Select<int>(context, cancellationToken: TestContext.Current.CancellationToken);
        int builderCompensations = 0;
        builder.Case(channel.Reader, (value, token) =>
            ValueTask.FromResult(Result.Ok(value).WithCompensation(_ =>
            {
                Interlocked.Increment(ref builderCompensations);
                return ValueTask.CompletedTask;
            })));

        var result = await builder.ExecuteAsync();
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(7);

        await RunCompensationAsync(scope);
        builderCompensations.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WaitGroupGo_ShouldAbsorbCompensations()
    {
        var (context, scope) = CreateContext("wg");
        var wg = new WaitGroup();

        int waitGroupCompensations = 0;
        wg.Go(context, (child, token) =>
        {
            return ValueTask.FromResult(Result.Ok(Unit.Value).WithCompensation(_ =>
            {
                Interlocked.Increment(ref waitGroupCompensations);
                return ValueTask.CompletedTask;
            }));
        });

        await wg.WaitAsync(TestContext.Current.CancellationToken);
        await RunCompensationAsync(scope);
        waitGroupCompensations.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TimerAdapters_ShouldRegisterCompensation()
    {
        var (context, scope) = CreateContext("timer");
        var result = await ResultPipelineTimers.DelayAsync(context, TimeSpan.Zero, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();

        var ticker = ResultPipelineTimers.NewTicker(context, TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
        await ticker.DisposeAsync();

        scope.HasActions.ShouldBeTrue();
        await RunCompensationAsync(scope);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask FanOut_ShouldDistributeToBranches()
    {
        var (context, scope) = CreateContext("fanout");
        var source = Channel.CreateUnbounded<int>();
        var readers = ResultPipelineChannels.FanOut(context, source.Reader, branchCount: 2, cancellationToken: TestContext.Current.CancellationToken);

        var testToken = TestContext.Current.CancellationToken;
        await source.Writer.WriteAsync(5, testToken);
        source.Writer.TryComplete();

        foreach (var reader in readers)
        {
            var value = await reader.ReadAsync(testToken);
            value.ShouldBe(5);
        }

        await RunCompensationAsync(scope);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask MergeWithStrategyAsync_ShouldHonorDelegateOrdering()
    {
        var (context, scope) = CreateContext("merge-strategy");
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();
        var destination = Channel.CreateUnbounded<int>();
        var token = TestContext.Current.CancellationToken;

        var readerTask = Task.Run(async () =>
        {
            var values = new List<int>();
            while (await destination.Reader.WaitToReadAsync(token))
            {
                while (destination.Reader.TryRead(out var value))
                {
                    values.Add(value);
                }
            }

            return values;
        }, token);

        var call = -1;
        Func<IReadOnlyList<ChannelReader<int>>, CancellationToken, ValueTask<int>> strategy = (_, _) =>
        {
            var next = Interlocked.Increment(ref call) & 1;
            return new ValueTask<int>(next);
        };

        var mergeTask = ResultPipelineChannels.MergeWithStrategyAsync(
            context,
            [first.Reader, second.Reader],
            destination.Writer,
            strategy,
            cancellationToken: token);

        await first.Writer.WriteAsync(1, token);
        await second.Writer.WriteAsync(2, token);
        first.Writer.TryComplete();
        second.Writer.TryComplete();

        var result = await mergeTask;
        result.IsSuccess.ShouldBeTrue();

        var merged = await readerTask;
        merged.ShouldBe(new[] { 1, 2 });
        await RunCompensationAsync(scope);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WindowAsync_ShouldFlushOnBatchOrInterval()
    {
        var provider = new FakeTimeProvider();
        var (context, scope) = CreateContext("window", provider);
        var source = Channel.CreateUnbounded<int>();
        var reader = await ResultPipelineChannels.WindowAsync(context, source.Reader, batchSize: 2, flushInterval: TimeSpan.FromSeconds(5), cancellationToken: TestContext.Current.CancellationToken);
        var token = TestContext.Current.CancellationToken;

        var batchesTask = Task.Run(async () =>
        {
            var batches = new List<IReadOnlyList<int>>();
            while (await reader.WaitToReadAsync(token))
            {
                while (reader.TryRead(out var batch))
                {
                    batches.Add(batch);
                }
            }

            return batches;
        }, token);

        await source.Writer.WriteAsync(1, token);
        await source.Writer.WriteAsync(2, token);
        await source.Writer.WriteAsync(3, token);

        provider.Advance(TimeSpan.FromSeconds(5));
        source.Writer.TryComplete();

        var batches = await batchesTask;
        batches.Count.ShouldBe(2);
        batches[0].ShouldBe([1, 2]);
        batches[1].ShouldBe([3]);

        await RunCompensationAsync(scope);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WindowAsync_ShouldFlushOnlyWhenSourceCompletes_WithInfiniteInterval()
    {
        var provider = new FakeTimeProvider();
        var (context, scope) = CreateContext("window-infinite", provider);
        var source = Channel.CreateUnbounded<int>();
        var token = TestContext.Current.CancellationToken;

        var window = ResultPipelineChannels.WindowAsync(
            context,
            source.Reader,
            batchSize: 3,
            flushInterval: Timeout.InfiniteTimeSpan,
            cancellationToken: token);

        await source.Writer.WriteAsync(1, token);
        await source.Writer.WriteAsync(2, token);
        source.Writer.TryComplete();
        var reader = await window;
        var batches = new List<IReadOnlyList<int>>();
        while (await reader.WaitToReadAsync(token))
        {
            while (reader.TryRead(out var batch))
            {
                batches.Add(batch);
            }
        }

        batches.Count.ShouldBeGreaterThan(0);
        batches.SelectMany(x => x).ShouldBe([1, 2]);

        await RunCompensationAsync(scope);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WindowAsync_ShouldPropagateCancellation()
    {
        var provider = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();
        var (context, scope) = CreateContext("window-cancel", provider);
        var source = Channel.CreateUnbounded<int>();
        var token = TestContext.Current.CancellationToken;

        var reader = ResultPipelineChannels.WindowAsync(
            context,
            source.Reader,
            batchSize: 2,
            flushInterval: TimeSpan.FromSeconds(1),
            cancellationToken: cts.Token);

        await source.Writer.WriteAsync(1, token);
        cts.Cancel();
        provider.Advance(TimeSpan.FromSeconds(5));
        source.Writer.TryComplete();

        await Should.ThrowAsync<OperationCanceledException>(async () => await (await reader).Completion.WaitAsync(TimeSpan.FromSeconds(2)));

        await RunCompensationAsync(scope);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WindowAsync_ShouldSurviveTimerBeforeData()
    {
        var provider = new FakeTimeProvider();
        var (context, scope) = CreateContext("window-timer-first", provider);
        var source = Channel.CreateUnbounded<int>();
        var reader = await ResultPipelineChannels.WindowAsync(context, source.Reader, batchSize: 2, flushInterval: TimeSpan.FromSeconds(1), cancellationToken: TestContext.Current.CancellationToken);

        // Fire the timer before any data arrives.
        provider.Advance(TimeSpan.FromSeconds(1));

        // Now send a full batch after the timer tick.
        await source.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        await source.Writer.WriteAsync(2, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var batches = new List<IReadOnlyList<int>>();
        while (await reader.WaitToReadAsync(TestContext.Current.CancellationToken))
        {
            while (reader.TryRead(out var batch))
            {
                batches.Add(batch);
            }
        }

        batches.ShouldHaveSingleItem();
        batches[0].ShouldBe([1, 2]);

        await RunCompensationAsync(scope);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TimeProviderDelay_ShouldRespectFakeTimeProviderAdvances()
    {
        var provider = new FakeTimeProvider();
        var waitTask = TimeProviderDelay.WaitAsync(provider, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        provider.Advance(TimeSpan.FromSeconds(1));

        var completed = await waitTask;
        completed.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ErrGroupAdapter_ShouldAbsorbPipelineResults()
    {
        var (context, scope) = CreateContext("errgroup");
        using var group = new ErrGroup();
        int compensationCount = 0;

        group.Go(context, (child, token) =>
        {
            return ValueTask.FromResult(Result.Fail<Unit>(Error.From("boom")).WithCompensation(_ =>
            {
                Interlocked.Increment(ref compensationCount);
                return ValueTask.CompletedTask;
            }));
        });

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);
        result.IsFailure.ShouldBeTrue();

        await RunCompensationAsync(scope);
        compensationCount.ShouldBe(1);
    }

    private static (ResultPipelineStepContext Context, CompensationScope Scope) CreateContext(string name, TimeProvider? provider = null)
    {
        var scope = new CompensationScope();
        var context = new ResultPipelineStepContext(name, scope, provider ?? TimeProvider.System, TestContext.Current.CancellationToken);
        return (context, scope);
    }

    private static async ValueTask RunCompensationAsync(CompensationScope scope)
    {
        var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
        await Result.RunCompensationAsync(policy, scope, TestContext.Current.CancellationToken);
    }
}
