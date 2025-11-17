using System.Threading.Channels;

using Shouldly;

namespace Hugo.Tests.Channels;

public sealed class ChannelCaseTests
{
    [Fact(Timeout = 15_000)]
    public async Task TryDequeueImmediately_ShouldContinueWithReadyValue()
    {
        var channel = Channel.CreateBounded<int>(1);
        channel.Writer.TryWrite(7);

        ChannelCase<int> channelCase = ChannelCase.Create(channel.Reader, (value, _) => ValueTask.FromResult(Result.Ok(value * 2)));

        var dequeued = channelCase.TryDequeueImmediately(out var state);

        dequeued.ShouldBeTrue();

        var result = await channelCase.ContinueWithAsync(state, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(14);
    }

    [Fact(Timeout = 15_000)]
    public async Task CreateDefault_ShouldSurfaceFailure_WhenCallbackThrows()
    {
        ChannelCase<int> channelCase = ChannelCase<int>.CreateDefault(_ => throw new InvalidOperationException("default-failure"));

        var result = await channelCase.ContinueWithAsync(null, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Message.ShouldContain("default-failure");
    }
}
