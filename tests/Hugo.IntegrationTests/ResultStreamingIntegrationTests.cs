using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Shouldly;

namespace Hugo.Tests;

public class ResultStreamingIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_ShouldMergeMultipleStreams()
    {
        var sourceA = Sequence([1, 2]);
        var sourceB = Sequence([3]);
        var writer = Channel.CreateUnbounded<Result<int>>();

        await Result.FanInAsync([sourceA, sourceB], writer.Writer, TestContext.Current.CancellationToken);

        var collected = await ReadAll(writer.Reader);

        collected.Count.ShouldBe(3);
        collected.Select(r => r.Value).OrderBy(v => v).ShouldBe([1, 2, 3]);
    }

    [Fact(Timeout = 15_000)]
    public async Task FanInAsync_ShouldPropagateCancellationAndFaultWriter()
    {
        using var cts = new CancellationTokenSource();
        var writer = Channel.CreateUnbounded<Result<int>>();

        var sources = new IAsyncEnumerable<Result<int>>[]
        {
            SlowSequence(cts.Token),
            Sequence([9])
        };

        var fanInTask = Result.FanInAsync(sources, writer.Writer, cts.Token).AsTask();

        cts.Cancel();

        var result = await fanInTask;
        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        await Should.ThrowAsync<OperationCanceledException>(async () => await writer.Reader.Completion);
    }

    [Fact(Timeout = 15_000)]
    public async Task ToChannelAsync_ShouldEmitCancellationSentinel()
    {
        using var cts = new CancellationTokenSource();
        var channel = Channel.CreateUnbounded<Result<int>>();

        var forwardTask = SlowSequence(cts.Token).ToChannelAsync(channel.Writer, cts.Token).AsTask();

        var collected = new List<Result<int>>();
        var readTask = Task.Run(async () =>
        {
            while (await channel.Reader.WaitToReadAsync(TestContext.Current.CancellationToken))
            {
                while (channel.Reader.TryRead(out var item))
                {
                    collected.Add(item);
                    if (collected.Count == 1)
                    {
                        cts.Cancel();
                    }
                }
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(forwardTask, readTask);

        collected.Count.ShouldBeGreaterThanOrEqualTo(1);
        collected.Last().IsFailure.ShouldBeTrue();
        collected.Last().Error?.Code.ShouldBe(ErrorCodes.Canceled);
        channel.Reader.Completion.IsCompleted.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task PartitionAsync_ShouldRouteResultsAndCompleteWriters()
    {
        var source = PartitionSource(TestContext.Current.CancellationToken);
        var high = Channel.CreateUnbounded<Result<int>>();
        var low = Channel.CreateUnbounded<Result<int>>();

        await source.PartitionAsync(value => value > 1, high.Writer, low.Writer, TestContext.Current.CancellationToken);

        var highs = await ReadAll(high.Reader);
        var lows = await ReadAll(low.Reader);

        highs.ShouldBe([Result.Ok(2)]);
        lows.Count.ShouldBe(2);
        lows[0].IsSuccess.ShouldBeTrue();
        lows[0].Value.ShouldBe(1);
        lows[1].IsFailure.ShouldBeTrue();
        await Task.WhenAll(high.Reader.Completion, low.Reader.Completion);
    }

    private static async IAsyncEnumerable<Result<int>> Sequence(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            await Task.Yield();
            yield return Result.Ok(value);
        }
    }

    private static async IAsyncEnumerable<Result<int>> PartitionSource([EnumeratorCancellation] CancellationToken token)
    {
        await Task.Yield();
        yield return Result.Ok(1);
        yield return Result.Ok(2);
        yield return Result.Fail<int>(Error.From("fail"));
        token.ThrowIfCancellationRequested();
    }

    private static async IAsyncEnumerable<Result<int>> SlowSequence([EnumeratorCancellation] CancellationToken token)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
            yield return Result.Ok(0);
        }
    }

    private static async Task<List<Result<int>>> ReadAll(ChannelReader<Result<int>> reader)
    {
        var list = new List<Result<int>>();
        while (await reader.WaitToReadAsync(TestContext.Current.CancellationToken))
        {
            while (reader.TryRead(out var item))
            {
                list.Add(item);
            }
        }
        return list;
    }
}
