using System.Threading.Channels;

using Shouldly;

namespace Hugo.Tests;

public sealed class ChannelCaseTests
{
    [Fact(Timeout = 10_000)]
    public async Task TryDequeueImmediately_ShouldReturnReadyDeferredRead()
    {
        var channel = Channel.CreateUnbounded<int>();
        await channel.Writer.WriteAsync(42, TestContext.Current.CancellationToken);

        var channelCase = ChannelCase.Create(channel.Reader, (int value, CancellationToken _) =>
            ValueTask.FromResult(Result.Ok(value)));

        channelCase.TryDequeueImmediately(out var state).ShouldBeTrue();
        state.ShouldBeOfType<DeferredRead<int>>();

        var result = await channelCase.ContinueWithAsync(state, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact(Timeout = 10_000)]
    public async Task CreateDefault_ShouldSurfaceExceptionsAsFailures()
    {
        var defaultCase = ChannelCase.CreateDefault<int>((Func<Result<int>>)(() => Result.Fail<int>(Error.FromException(new InvalidOperationException("failure")))));

        var (hasValue, state) = await defaultCase.WaitAsync(TestContext.Current.CancellationToken);
        hasValue.ShouldBeTrue();

        var result = await defaultCase.ContinueWithAsync(state, TestContext.Current.CancellationToken);
        result.IsFailure.ShouldBeTrue();
        result.Error?.Message.ShouldContain("failure");
    }

    [Fact(Timeout = 10_000)]
    public async Task WithPriority_ShouldUpdatePriorityPreservingDelegates()
    {
        var channel = Channel.CreateUnbounded<int>();
        await channel.Writer.WriteAsync(1, TestContext.Current.CancellationToken);

        var original = ChannelCase.Create(channel.Reader, (int value, CancellationToken _) =>
            ValueTask.FromResult(Result.Ok(value)));
        var elevated = original.WithPriority(5);

        elevated.Equals(original).ShouldBeFalse();
        elevated.TryDequeueImmediately(out var state).ShouldBeTrue();

        var result = await elevated.ContinueWithAsync(state, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 10_000)]
    public void TryDequeueImmediately_ShouldReturnFalseWhenNoProbe()
    {
        var channelCase = new ChannelCase<int>(
            _ => Task.FromResult((false, (object?)null)),
            (_, _) => ValueTask.FromResult(Result.Ok(0)),
            readyProbe: null,
            priority: 0,
            isDefault: false);

        channelCase.TryDequeueImmediately(out var state).ShouldBeFalse();
        state.ShouldBeNull();
    }
}
