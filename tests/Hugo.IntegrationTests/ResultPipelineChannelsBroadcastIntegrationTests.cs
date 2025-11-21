using System.Threading.Channels;

using Hugo.Policies;

using Microsoft.Extensions.Time.Testing;


namespace Hugo.Tests;

public sealed class ResultPipelineChannelsBroadcastIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task BroadcastAsync_ShouldReplicateValuesAndCompleteDestinations()
    {
        var provider = new FakeTimeProvider();
        var scope = new CompensationScope();
        var context = new ResultPipelineStepContext("broadcast", scope, provider, TestContext.Current.CancellationToken);

        var source = Channel.CreateUnbounded<int>();
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();

        await source.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        await source.Writer.WriteAsync(2, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var result = await ResultPipelineChannels.BroadcastAsync(
            context,
            source.Reader,
            new[] { first.Writer, second.Writer },
            completeDestinations: true,
            cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();

        var firstValues = await ReadAll(first.Reader);
        var secondValues = await ReadAll(second.Reader);

        firstValues.ShouldBe([1, 2]);
        secondValues.ShouldBe([1, 2]);
        scope.HasActions.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task BroadcastAsync_ShouldReturnCanceledResult_WhenCallerCancels()
    {
        var provider = new FakeTimeProvider();
        var scope = new CompensationScope();
        var context = new ResultPipelineStepContext("broadcast-cancel", scope, provider, TestContext.Current.CancellationToken);

        var source = Channel.CreateUnbounded<int>();
        var destination = Channel.CreateUnbounded<int>();

        using var cts = new CancellationTokenSource(25);

        var result = await ResultPipelineChannels.BroadcastAsync(
            context,
            source.Reader,
            new[] { destination.Writer },
            completeDestinations: true,
            cancellationToken: cts.Token);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);

        await Should.ThrowAsync<OperationCanceledException>(async () => await destination.Reader.Completion);
    }

    private static async Task<List<int>> ReadAll(ChannelReader<int> reader)
    {
        var list = new List<int>();
        while (await reader.WaitToReadAsync(TestContext.Current.CancellationToken))
        {
            while (reader.TryRead(out var value))
            {
                list.Add(value);
            }
        }

        return list;
    }
}
