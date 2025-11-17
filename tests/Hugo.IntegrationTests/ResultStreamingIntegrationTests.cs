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

    private static async IAsyncEnumerable<Result<int>> Sequence(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            await Task.Yield();
            yield return Result.Ok(value);
        }
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
