using System.Threading.Channels;

using Shouldly;

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
}
