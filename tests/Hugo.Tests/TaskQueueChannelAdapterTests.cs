using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests;

[Collection("TaskQueueConcurrency")]
internal class TaskQueueChannelAdapterTests
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
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Create_ShouldSurfaceLeasesAndAllowCompletion()
    {
        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(5),
            HeartbeatInterval = TimeSpan.Zero,
            LeaseSweepInterval = TimeSpan.FromMilliseconds(50),
            RequeueDelay = TimeSpan.FromMilliseconds(100)
        };

        await using var queue = new TaskQueue<string>(options, new FakeTimeProvider()).ConfigureAwait(false);
        await queue.EnqueueAsync("alpha", TestContext.Current.CancellationToken).ConfigureAwait(false);

        var adapter = TaskQueueChannelAdapter<string>.Create(queue);
        var lease = await adapter.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        Assert.Equal("alpha", lease.Value);
        await lease.CompleteAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        await adapter.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task Dispose_ShouldCompleteChannel()
    {
        await using var queue = new TaskQueue<string>().ConfigureAwait(false);
        await queue.EnqueueAsync("beta", TestContext.Current.CancellationToken).ConfigureAwait(false);

        var adapter = TaskQueueChannelAdapter<string>.Create(queue);
        await adapter.DisposeAsync().ConfigureAwait(false);

        await adapter.Reader.Completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        Assert.False(adapter.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Create_WithConcurrency_ShouldPumpMultipleLeases()
    {
        await using var queue = new TaskQueue<string>().ConfigureAwait(false);
        await queue.EnqueueAsync("one", TestContext.Current.CancellationToken).ConfigureAwait(false);
        await queue.EnqueueAsync("two", TestContext.Current.CancellationToken).ConfigureAwait(false);

        var adapter = TaskQueueChannelAdapter<string>.Create(queue, concurrency: 2);

        var first = await adapter.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        var second = await adapter.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        Assert.NotEqual(first.Value, second.Value);

        await first.CompleteAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await second.CompleteAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        await adapter.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
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

        await using var queue = new TaskQueue<string>(options, provider).ConfigureAwait(false);
        var adapter = TaskQueueChannelAdapter<string>.Create(queue);

        await queue.EnqueueAsync("gamma", TestContext.Current.CancellationToken).ConfigureAwait(false);
        var lease = await adapter.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        provider.Advance(TimeSpan.FromMilliseconds(75));
        provider.Advance(TimeSpan.FromMilliseconds(75));

        var requeued = await adapter.Reader.ReadAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        Assert.Equal(2, requeued.Attempt);
        Assert.Equal("gamma", requeued.Value);
        await requeued.CompleteAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        await adapter.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task QueueDisposal_ShouldCompleteChannel()
    {
        await using var queue = new TaskQueue<string>().ConfigureAwait(false);
        var adapter = TaskQueueChannelAdapter<string>.Create(queue);

        await queue.DisposeAsync().ConfigureAwait(false);

        await adapter.Reader.Completion.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
        await adapter.DisposeAsync().ConfigureAwait(false);
    }

    [Fact]
    public async Task Dispose_WithOwnership_ShouldDisposeQueue()
    {
        var queue = new TaskQueue<string>();
        var adapter = TaskQueueChannelAdapter<string>.Create(queue, ownsQueue: true);

        await adapter.DisposeAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await queue.EnqueueAsync("delta", TestContext.Current.CancellationToken).ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Fact]
    public async Task Dispose_WhileLeaseBuffered_ShouldRequeueWork()
    {
        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(5),
            HeartbeatInterval = TimeSpan.Zero,
            LeaseSweepInterval = TimeSpan.FromMilliseconds(50)
        };

        await using var queue = new TaskQueue<string>(options).ConfigureAwait(false);
        var channel = Channel.CreateUnbounded<TaskQueueLease<string>>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        await using var adapter = TaskQueueChannelAdapter<string>.Create(queue, channel).ConfigureAwait(false);

        channel.Writer.TryComplete();

        await queue.EnqueueAsync("pending", TestContext.Current.CancellationToken).ConfigureAwait(false);

        await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken).ConfigureAwait(false);

        using var leaseCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        leaseCts.CancelAfter(TimeSpan.FromSeconds(5));

        var lease = await queue.LeaseAsync(leaseCts.Token).ConfigureAwait(false);

        Assert.Equal("pending", lease.Value);
        Assert.Equal(2, lease.Attempt);

        await lease.CompleteAsync(leaseCts.Token).ConfigureAwait(false);
    }
}
