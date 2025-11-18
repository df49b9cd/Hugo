using System.Threading.Channels;

using Hugo.Policies;

using Microsoft.Extensions.Time.Testing;

using Shouldly;

namespace Hugo.Tests;

public sealed class ResultPipelineChannelsIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task MergeWithStrategyAsync_ShouldSwitchReadersAndCompleteDestination()
    {
        var provider = new FakeTimeProvider();
        var scope = new CompensationScope();
        var context = new ResultPipelineStepContext("merge-with-strategy", scope, provider, TestContext.Current.CancellationToken);

        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();
        var destination = Channel.CreateUnbounded<int>();

        await first.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        await first.Writer.WriteAsync(2, TestContext.Current.CancellationToken);
        await first.Writer.WriteAsync(3, TestContext.Current.CancellationToken);
        first.Writer.TryComplete();

        await second.Writer.WriteAsync(99, TestContext.Current.CancellationToken);
        await second.Writer.WriteAsync(100, TestContext.Current.CancellationToken);
        second.Writer.TryComplete();

        var invalidReturned = false;
        var preferredIndex = 0;

        ValueTask<int> SelectionStrategy(IReadOnlyList<ChannelReader<int>> readers, CancellationToken ct)
        {
            if (!invalidReturned)
            {
                invalidReturned = true;
                return ValueTask.FromResult(-1); // exercise invalid-index branch
            }

            if (!readers[preferredIndex].Completion.IsCompleted)
            {
                return ValueTask.FromResult(preferredIndex);
            }

            preferredIndex = (preferredIndex + 1) % readers.Count;
            return ValueTask.FromResult(preferredIndex);
        }

        var mergeTask = ResultPipelineChannels.MergeWithStrategyAsync(
            context,
            [first.Reader, second.Reader],
            destination.Writer,
            SelectionStrategy,
            cancellationToken: TestContext.Current.CancellationToken);

        var collected = new List<int>();

        await foreach (var value in destination.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            collected.Add(value);
        }

        var mergeResult = await mergeTask;

        mergeResult.IsSuccess.ShouldBeTrue();
        collected.ShouldBe([1, 2, 3, 99, 100]);
        destination.Reader.Completion.IsCompleted.ShouldBeTrue();
        scope.HasActions.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task MergeWithStrategyAsync_ShouldPropagateCancellation()
    {
        var provider = new FakeTimeProvider();
        var scope = new CompensationScope();
        var context = new ResultPipelineStepContext("merge-cancel", scope, provider, TestContext.Current.CancellationToken);

        var source = Channel.CreateUnbounded<int>();
        var destination = Channel.CreateUnbounded<int>();
        using var cts = new CancellationTokenSource();

        var mergeTask = ResultPipelineChannels.MergeWithStrategyAsync(
            context,
            [source.Reader],
            destination.Writer,
            (_, _) => ValueTask.FromResult(0),
            cancellationToken: cts.Token);

        cts.Cancel();

        var result = await mergeTask;

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        destination.Reader.Completion.IsCompleted.ShouldBeTrue();
    }
}
