using System.Threading.Channels;


namespace Hugo.Tests;

public sealed class ChannelCaseIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task TryDequeueImmediately_ShouldReturnReadyValue()
    {
        var channel = Channel.CreateUnbounded<int>();
        await channel.Writer.WriteAsync(3, TestContext.Current.CancellationToken);

        ChannelCase<int> @case = ChannelCase.Create(channel.Reader, static (value, _) => ValueTask.FromResult(Result.Ok(value + 1)));

        var ready = @case.TryDequeueImmediately(out var state);

        ready.ShouldBeTrue();

        var result = await @case.ContinueWithAsync(state, TestContext.Current.CancellationToken);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(4);
    }

    [Fact(Timeout = 15_000)]
    public async Task CreateDefault_ShouldSurfaceFailure()
    {
        ChannelCase<int> @case = ChannelCase.CreateDefault<int>(static () => Result.Fail<int>(Error.From("boom")));

        var result = await @case.ContinueWithAsync(null, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Message.ShouldContain("boom");
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitAsync_ShouldReturnFalse_WhenChannelCompletesBeforeRead()
    {
        var channel = Channel.CreateUnbounded<int>();
        channel.Writer.TryComplete();

        ChannelCase<int> @case = ChannelCase.Create(channel.Reader, static (value, _) => ValueTask.FromResult(Result.Ok(value)));

        var (hasValue, state) = await @case.WaitAsync(TestContext.Current.CancellationToken);

        hasValue.ShouldBeFalse();
        state.ShouldBeNull();
    }

    [Fact(Timeout = 15_000)]
    public async Task ContinueWithAsync_ShouldReturnSelectDrained_WhenChannelClosedBeforeRead()
    {
        var channel = Channel.CreateUnbounded<int>();
        channel.Writer.TryComplete();

        ChannelCase<int> @case = ChannelCase.Create(channel.Reader, static (value, _) => ValueTask.FromResult(Result.Ok(value)));

        var result = await @case.ContinueWithAsync(new DeferredRead<int>(channel.Reader), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.SelectDrained);
    }

    [Fact(Timeout = 15_000)]
    public async Task ContinueWithAsync_ShouldReturnError_WhenStateIsInvalid()
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
}
