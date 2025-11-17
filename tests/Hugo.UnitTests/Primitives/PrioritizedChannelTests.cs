using System;
using System.Threading.Channels;

namespace Hugo.Tests.Primitives;

public sealed class PrioritizedChannelTests
{
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WriteReleaseTimeout = TimeSpan.FromSeconds(1);

    [Fact(Timeout = 15_000)]
    public async ValueTask PrioritizedChannel_ShouldRespectCapacity_WhenReaderSlow()
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

    [Fact(Timeout = 15_000)]
    public async ValueTask PrioritizedChannel_ShouldDrainSingleItemPerLane()
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

    [Fact(Timeout = 15_000)]
    public async ValueTask PrioritizedChannel_WaitToReadAsync_ShouldRespectCancellation()
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

    [Fact(Timeout = 15_000)]
    public async ValueTask PrioritizedChannel_ShouldPropagateLaneException()
    {
        var failingLane = new AsyncLaneReader();
        var reader = CreatePrioritizedReader(failingLane);
        var ct = TestContext.Current.CancellationToken;

        var waitTask = reader.WaitToReadAsync(ct).AsTask();
        failingLane.Fail(new InvalidOperationException("lane failed"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => waitTask);
        Assert.Equal("lane failed", ex.Message);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask PrioritizedChannel_ShouldObserveCancelledLane()
    {
        var cancelingLane = new AsyncLaneReader();
        var reader = CreatePrioritizedReader(cancelingLane);
        var ct = TestContext.Current.CancellationToken;

        var waitTask = reader.WaitToReadAsync(ct).AsTask();
        cancelingLane.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => waitTask);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask PrioritizedChannel_ShouldPropagateSynchronousLaneFault()
    {
        var lane = new ImmediateFaultLaneReader(new InvalidOperationException("sync fault"));
        var reader = CreatePrioritizedReader(lane);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.WaitToReadAsync(TestContext.Current.CancellationToken).AsTask());
        Assert.Equal("sync fault", ex.Message);
    }

    private static PrioritizedChannel<int>.PrioritizedChannelReader CreatePrioritizedReader(params ChannelReader<int>[] lanes) =>
        new(lanes, prefetchPerPriority: 1);

    private sealed class AsyncLaneReader : ChannelReader<int>
    {
        private readonly TaskCompletionSource<bool> _waitSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask Completion => _completion.Task;

        public override bool TryRead(out int item)
        {
            item = default;
            return false;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            new(_waitSource.Task);

        public void Fail(Exception exception)
        {
            _waitSource.TrySetException(exception);
            _completion.TrySetException(exception);
        }

        public void Cancel()
        {
            var ex = new OperationCanceledException("lane canceled");
            _waitSource.TrySetException(ex);
            _completion.TrySetCanceled();
        }
    }

    private sealed class ImmediateFaultLaneReader : ChannelReader<int>
    {
        private readonly Exception _exception;

        public ImmediateFaultLaneReader(Exception exception)
        {
            _exception = exception ?? throw new ArgumentNullException(nameof(exception));
        }

        public override ValueTask Completion => Task.FromException(_exception);

        public override bool TryRead(out int item)
        {
            item = default;
            return false;
        }

        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default) =>
            new(Task.FromException<bool>(_exception));
    }
}
