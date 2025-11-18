using System.Threading.Channels;

using Shouldly;

using Hugo.Policies;

using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public sealed class ResultPipelineChannelsEdgeTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask FanInAsync_ValueTaskOverload_ShouldAbsorbResult()
    {
        var context = new ResultPipelineStepContext("fanIn", new CompensationScope(), TimeProvider.System, TestContext.Current.CancellationToken);
        var channel = Channel.CreateUnbounded<int>();

        await channel.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        await channel.Writer.WriteAsync(2, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var result = await ResultPipelineChannels.FanInAsync(
            context,
            new[] { channel.Reader },
            value => ValueTask.FromResult(Result.Ok(Unit.Value)),
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 5_000)]
    public void WindowAsync_ShouldValidateFlushInterval()
    {
        var context = new ResultPipelineStepContext("window", new CompensationScope(), TimeProvider.System, TestContext.Current.CancellationToken);
        var source = Channel.CreateUnbounded<int>();

        Should.Throw<ArgumentOutOfRangeException>(() =>
            ResultPipelineChannels.WindowAsync(context, source.Reader, batchSize: 1, flushInterval: TimeSpan.FromMilliseconds(-2)));
    }
}
