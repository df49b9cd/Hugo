using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Hugo;
using Hugo.Policies;

using Xunit;

using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public class ResultPipelineAdaptersTests
{
    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldAbsorbCompensation()
    {
        var channel = Channel.CreateUnbounded<int>();
        var testToken = TestContext.Current.CancellationToken;

        await channel.Writer.WriteAsync(1, testToken);
        channel.Writer.TryComplete();

        var (context, scope) = CreateContext("select-test");
        int compensationInvocations = 0;

        var cases = new[]
        {
            ChannelCase<int>.Create(channel.Reader, (value, ct) =>
                ValueTask.FromResult(
                    Result.Ok(value)
                        .WithCompensation(_ =>
                        {
                            Interlocked.Increment(ref compensationInvocations);
                            return ValueTask.CompletedTask;
                        })))
        };

        var result = await ResultPipelineChannels.SelectAsync(context, cases, cancellationToken: testToken);

        Assert.True(result.IsSuccess);
        Assert.True(scope.HasActions);

        var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
        await Result.RunCompensationAsync(policy, scope, testToken);
        Assert.Equal(1, compensationInvocations);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_ShouldTrackCompensationPerValue()
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
            new[] { channelA.Reader, channelB.Reader },
            handler,
            cancellationToken: testToken);

        Assert.True(fanInResult.IsSuccess);
        Assert.True(scope.HasActions);

        var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
        await Result.RunCompensationAsync(policy, scope, testToken);
        Assert.Equal(2, compensationCount);
    }

    [Fact(Timeout = 15_000)]
    public async Task RetryAsync_ShouldRetryUntilSuccess()
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

        Assert.True(result.IsSuccess);
        Assert.Equal(43, result.Value + 1);
        Assert.Equal(3, attempts);
    }

    [Fact(Timeout = 15_000)]
    public async Task WithTimeoutAsync_ShouldReturnTimeoutError()
    {
        var result = await ResultPipeline.WithTimeoutAsync(
            async (_, token) =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), token);
                return Result.Ok(1);
            },
            timeout: TimeSpan.FromMilliseconds(10),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Timeout, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectBuilder_ShouldComposeCases()
    {
        var (context, scope) = CreateContext("builder");
        var channel = Channel.CreateUnbounded<int>();
        await channel.Writer.WriteAsync(7, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var builder = ResultPipelineChannels.Select<int>(context);
        int builderCompensations = 0;
        builder.Case(channel.Reader, (value, token) =>
            Task.FromResult(Result.Ok(value).WithCompensation(_ =>
            {
                Interlocked.Increment(ref builderCompensations);
                return ValueTask.CompletedTask;
            })));

        var result = await builder.ExecuteAsync();
        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);

        await RunCompensationAsync(scope);
        Assert.Equal(1, builderCompensations);
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitGroupGo_ShouldAbsorbCompensations()
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
        Assert.Equal(1, waitGroupCompensations);
    }

    [Fact(Timeout = 15_000)]
    public async Task TimerAdapters_ShouldRegisterCompensation()
    {
        var (context, scope) = CreateContext("timer");
        var result = await ResultPipelineTimers.DelayAsync(context, TimeSpan.Zero, TestContext.Current.CancellationToken);
        Assert.True(result.IsSuccess);

        var ticker = ResultPipelineTimers.NewTicker(context, TimeSpan.FromMilliseconds(5), TestContext.Current.CancellationToken);
        await ticker.DisposeAsync();

        Assert.True(scope.HasActions);
        await RunCompensationAsync(scope);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanOut_ShouldDistributeToBranches()
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
            Assert.Equal(5, value);
        }

        await RunCompensationAsync(scope);
    }

    [Fact(Timeout = 15_000)]
    public async Task ErrGroupAdapter_ShouldAbsorbPipelineResults()
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
        Assert.True(result.IsFailure);

        await RunCompensationAsync(scope);
        Assert.Equal(1, compensationCount);
    }

    private static (ResultPipelineStepContext Context, CompensationScope Scope) CreateContext(string name)
    {
        var scope = new CompensationScope();
        var context = new ResultPipelineStepContext(name, scope, TimeProvider.System, TestContext.Current.CancellationToken);
        return (context, scope);
    }

    private static async Task RunCompensationAsync(CompensationScope scope)
    {
        var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
        await Result.RunCompensationAsync(policy, scope, TestContext.Current.CancellationToken);
    }
}
