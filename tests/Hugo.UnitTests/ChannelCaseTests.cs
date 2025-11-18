using System.Threading.Channels;

using Shouldly;

namespace Hugo.Tests;

public sealed class ChannelCaseTests
{
    [Fact(Timeout = 15_000)]
    public async Task WaitAsync_ShouldReturnFalse_WhenChannelCompletedBeforeRead()
    {
        var channel = Channel.CreateUnbounded<int>();
        channel.Writer.TryComplete();

        ChannelCase<int> @case = ChannelCase.Create(channel.Reader, static (value, _) => ValueTask.FromResult(Result.Ok(value)));

        var (hasValue, state) = await @case.WaitAsync(TestContext.Current.CancellationToken);

        hasValue.ShouldBeFalse();
        state.ShouldBeNull();
    }

    [Fact(Timeout = 15_000)]
    public async Task ContinueWithAsync_ShouldReturnSelectDrained_WhenChannelClosesBeforeRead()
    {
        var channel = Channel.CreateUnbounded<int>();
        channel.Writer.TryComplete();

        ChannelCase<int> @case = ChannelCase.Create(channel.Reader, static (value, _) => ValueTask.FromResult(Result.Ok(value)));

        var result = await @case.ContinueWithAsync(new DeferredRead<int>(channel.Reader), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.SelectDrained);
    }

    [Fact(Timeout = 15_000)]
    public async Task ContinueWithAsync_ShouldReturnError_WhenStateIsUnexpected()
    {
        var channel = Channel.CreateUnbounded<int>();
        ChannelCase<int> @case = ChannelCase.Create(channel.Reader, static (value, _) => ValueTask.FromResult(Result.Ok(value)));

        var result = await @case.ContinueWithAsync(new object(), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
    }

    [Fact]
    public void WithContinuation_ShouldThrow_WhenContinuationIsNull()
    {
        ChannelCase<int> @case = new(static _ => Task.FromResult((true, (object?)null)), static (_, _) => ValueTask.FromResult(Result.Ok(0)), null, priority: 0, isDefault: false);

        Should.Throw<ArgumentNullException>(() => @case.WithContinuation(null!));
    }

    [Fact]
    public void TryDequeueImmediately_ShouldReturnFalse_WhenProbeUnavailable()
    {
        ChannelCase<int> @case = new(static _ => Task.FromResult((true, (object?)null)), static (_, _) => ValueTask.FromResult(Result.Ok(0)), readyProbe: null, priority: 0, isDefault: false);

        var ready = @case.TryDequeueImmediately(out var state);

        ready.ShouldBeFalse();
        state.ShouldBeNull();
    }
}
