using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using System.Threading.Tasks;
using Shouldly;

using Hugo;

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

    private static async IAsyncEnumerable<Result<int>> Sequence(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            await Task.Yield();
            yield return Result.Ok(value);
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
