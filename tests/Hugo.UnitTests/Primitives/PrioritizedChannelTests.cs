using System;
using System.Threading.Channels;

namespace Hugo.Tests.Primitives;

public sealed class PrioritizedChannelTests
{
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WriteReleaseTimeout = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task PrioritizedChannel_ShouldRespectCapacity_WhenReaderSlow()
    {
        var channel = new PrioritizedChannel<int>(new PrioritizedChannelOptions
        {
            PriorityLevels = 1,
            CapacityPerLevel = 2,
            PrefetchPerPriority = 1,
            FullMode = BoundedChannelFullMode.Wait
        });

        var writer = channel.PrioritizedWriter;
        var reader = channel.Reader;
        var ct = TestContext.Current.CancellationToken;

        await writer.WriteAsync(1, priority: 0, ct);
        await writer.WriteAsync(2, priority: 0, ct);

        Assert.True(await reader.WaitToReadAsync(ct));

        await writer.WriteAsync(3, priority: 0, ct);

        var blockedWrite = writer.WriteAsync(4, priority: 0, ct).AsTask();
        await Task.Delay(ShortDelay, ct);
        Assert.False(blockedWrite.IsCompleted);

        Assert.True(reader.TryRead(out var first));
        Assert.Equal(1, first);

        Assert.True(reader.TryRead(out var second));
        Assert.Equal(2, second);

        Assert.True(reader.TryRead(out var third));
        Assert.Equal(3, third);

        await blockedWrite.WaitAsync(WriteReleaseTimeout, ct);
        Assert.True(reader.TryRead(out var fourth));
        Assert.Equal(4, fourth);
    }

    [Fact]
    public async Task PrioritizedChannel_ShouldDrainSingleItemPerLane()
    {
        var channel = new PrioritizedChannel<int>(new PrioritizedChannelOptions
        {
            PriorityLevels = 3,
            CapacityPerLevel = 4,
            PrefetchPerPriority = 1
        });

        var writer = channel.PrioritizedWriter;
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 3; i++)
        {
            await writer.WriteAsync(10 + i, priority: 0, ct);
        }

        for (var i = 0; i < 2; i++)
        {
            await writer.WriteAsync(20 + i, priority: 1, ct);
        }

        Assert.True(await channel.Reader.WaitToReadAsync(ct));

        var prioritizedReader = channel.PrioritizedReader;
        Assert.Equal(2, prioritizedReader.BufferedItemCount);
        Assert.Equal(1, prioritizedReader.GetBufferedCountForPriority(0));
        Assert.Equal(1, prioritizedReader.GetBufferedCountForPriority(1));
        Assert.Equal(0, prioritizedReader.GetBufferedCountForPriority(2));
    }

    [Fact]
    public async Task PrioritizedChannel_WaitToReadAsync_ShouldRespectCancellation()
    {
        var channel = new PrioritizedChannel<int>(new PrioritizedChannelOptions
        {
            PriorityLevels = 1,
            PrefetchPerPriority = 1
        });

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
        {
            _ = await channel.Reader.WaitToReadAsync(cts.Token);
        });
    }

    [Fact]
    public async Task PrioritizedChannel_WaitToReadSlowPath_ShouldAvoidExtraAllocations()
    {
        var channel = new PrioritizedChannel<int>(new PrioritizedChannelOptions
        {
            PriorityLevels = 2,
            PrefetchPerPriority = 1
        });

        var reader = channel.Reader;
        var writer = channel.PrioritizedWriter;
        var ct = TestContext.Current.CancellationToken;

        var waitTask = reader.WaitToReadAsync(ct);
        var baseline = GC.GetAllocatedBytesForCurrentThread();

        var producer = Task.Run(async () =>
        {
            await Task.Delay(10, ct);
            await writer.WriteAsync(42, priority: 1, ct);
        }, ct);

        Assert.True(await waitTask.AsTask());
        await producer;

        var allocated = GC.GetAllocatedBytesForCurrentThread() - baseline;
        Assert.InRange(allocated, 0, 4 * 1024);
    }
}
