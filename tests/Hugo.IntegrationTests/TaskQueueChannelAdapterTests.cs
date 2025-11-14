using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests;

[Collection("TaskQueueConcurrency")]
public class TaskQueueChannelAdapterTests
{
    private static async Task WaitForConditionAsync(Func<bool> condition, CancellationToken cancellationToken, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(10);
        var spin = new SpinWait();

        for (var i = 0; i < 128; i++)
        {
            if (condition())
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            spin.SpinOnce();
        }

        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(interval, cancellationToken);
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Create_ShouldSurfaceLeasesAndAllowCompletion()
    {
        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(5),
            HeartbeatInterval = TimeSpan.Zero,
            LeaseSweepInterval = TimeSpan.FromMilliseconds(50),
            RequeueDelay = TimeSpan.FromMilliseconds(100)
        };

        await using var queue = new TaskQueue<string>(options);
        await queue.EnqueueAsync("alpha", TestContext.Current.CancellationToken);

        var adapter = TaskQueueChannelAdapter<string>.Create(queue);
        var lease = await adapter.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("alpha", lease.Value);
        await lease.CompleteAsync(TestContext.Current.CancellationToken);

        await adapter.DisposeAsync();
    }

    [Fact(Timeout = 15_000)]
    public async Task Dispose_ShouldCompleteChannel()
    {
        await using var queue = new TaskQueue<string>();
        await queue.EnqueueAsync("beta", TestContext.Current.CancellationToken);

        var adapter = TaskQueueChannelAdapter<string>.Create(queue);
        await adapter.DisposeAsync();

        await adapter.Reader.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(adapter.Reader.TryRead(out _));
    }

    [Fact(Timeout = 15_000)]
    public async Task Create_WithConcurrency_ShouldPumpMultipleLeases()
    {
        await using var queue = new TaskQueue<string>();
        await queue.EnqueueAsync("one", TestContext.Current.CancellationToken);
        await queue.EnqueueAsync("two", TestContext.Current.CancellationToken);

        var adapter = TaskQueueChannelAdapter<string>.Create(queue, concurrency: 2);

        var first = await adapter.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var second = await adapter.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Value, second.Value);

        await first.CompleteAsync(TestContext.Current.CancellationToken);
        await second.CompleteAsync(TestContext.Current.CancellationToken);

        await adapter.DisposeAsync();
    }

    [Fact(Timeout = 15_000)]
    public async Task Adapter_ShouldRequeueExpiredLeases()
    {
        var provider = new FakeTimeProvider();
        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(50),
            LeaseSweepInterval = TimeSpan.FromMilliseconds(10),
            HeartbeatInterval = TimeSpan.Zero,
            MaxDeliveryAttempts = 2
        };

        await using var queue = new TaskQueue<string>(options, provider);
        var adapter = TaskQueueChannelAdapter<string>.Create(queue);

        await queue.EnqueueAsync("gamma", TestContext.Current.CancellationToken);
        var lease = await adapter.Reader.ReadAsync(TestContext.Current.CancellationToken);

        provider.Advance(TimeSpan.FromMilliseconds(75));
        provider.Advance(TimeSpan.FromMilliseconds(75));

        var requeued = await adapter.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, requeued.Attempt);
        Assert.Equal("gamma", requeued.Value);
        await requeued.CompleteAsync(TestContext.Current.CancellationToken);

        await adapter.DisposeAsync();
    }

    [Fact(Timeout = 15_000)]
    public async Task QueueDisposal_ShouldCompleteChannel()
    {
        await using var queue = new TaskQueue<string>();
        var adapter = TaskQueueChannelAdapter<string>.Create(queue);

        await queue.DisposeAsync();

        await adapter.Reader.Completion.WaitAsync(TestContext.Current.CancellationToken);
        await adapter.DisposeAsync();
    }

    [Fact(Timeout = 15_000)]
    public async Task Dispose_WithOwnership_ShouldDisposeQueue()
    {
        await using var queue = new TaskQueue<string>();
        var adapter = TaskQueueChannelAdapter<string>.Create(queue, ownsQueue: true);

        await adapter.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await queue.EnqueueAsync("delta", TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task Dispose_WhileLeaseBuffered_ShouldRequeueWork()
    {
        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(5),
            HeartbeatInterval = TimeSpan.Zero,
            LeaseSweepInterval = TimeSpan.FromMilliseconds(50)
        };

        await using var queue = new TaskQueue<string>(options);
        var adapter = TaskQueueChannelAdapter<string>.Create(queue);
        TaskQueueLease<string>? lease = null;

        try
        {
            await queue.EnqueueAsync("pending", TestContext.Current.CancellationToken);
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            waitCts.CancelAfter(TimeSpan.FromSeconds(5));
            await WaitForConditionAsync(() => queue.ActiveLeaseCount == 1, waitCts.Token);
        }
        finally
        {
            await adapter.DisposeAsync();
            lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        }

        Assert.NotNull(lease);
        Assert.Equal("pending", lease!.Value);
        Assert.Equal(2, lease.Attempt);

        await lease.CompleteAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 15_000)]
    public async Task Create_ShouldBoundActiveLeases_WhenReaderSlow()
    {
        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(5),
            HeartbeatInterval = TimeSpan.Zero,
            LeaseSweepInterval = TimeSpan.FromMilliseconds(50)
        };

        await using var queue = new TaskQueue<string>(options, new FakeTimeProvider());
        for (var i = 0; i < 10; i++)
        {
            await queue.EnqueueAsync($"payload-{i}", TestContext.Current.CancellationToken);
        }

        const int concurrency = 3;
        await using (var adapter = TaskQueueChannelAdapter<string>.Create(queue, concurrency: concurrency))
        {
            var maxObserved = 0;
            for (var i = 0; i < 25; i++)
            {
                var current = queue.ActiveLeaseCount;
                maxObserved = Math.Max(maxObserved, current);
                Assert.InRange(current, 0, concurrency);
                await Task.Delay(TimeSpan.FromMilliseconds(40), TestContext.Current.CancellationToken);
            }

            Assert.True(maxObserved > 0, "Expected at least one outstanding lease while pumps were running.");
        }

    }
}
