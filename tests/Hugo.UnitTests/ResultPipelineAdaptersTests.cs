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

        var scope = new CompensationScope();
        var context = new ResultPipelineStepContext("select-test", scope, TimeProvider.System, testToken);
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

        var scope = new CompensationScope();
        var context = new ResultPipelineStepContext("fanin-test", scope, TimeProvider.System, testToken);
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
}
