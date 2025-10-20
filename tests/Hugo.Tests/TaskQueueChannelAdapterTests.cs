using System.Threading.Channels;
using Hugo;
using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests;

public class TaskQueueChannelAdapterTests
{
    [Fact]
    public async Task Create_ShouldSurfaceLeasesAndAllowCompletion()
    {
        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(5),
            HeartbeatInterval = TimeSpan.Zero,
            LeaseSweepInterval = TimeSpan.FromMilliseconds(50)
        };

        await using var queue = new TaskQueue<string>(options, new FakeTimeProvider());
        await queue.EnqueueAsync("alpha", TestContext.Current.CancellationToken);

        var adapter = TaskQueueChannelAdapter<string>.Create(queue);
        var lease = await adapter.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("alpha", lease.Value);
        await lease.CompleteAsync(TestContext.Current.CancellationToken);

        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_ShouldCompleteChannel()
    {
        await using var queue = new TaskQueue<string>();
        await queue.EnqueueAsync("beta", TestContext.Current.CancellationToken);

        var adapter = TaskQueueChannelAdapter<string>.Create(queue);
        await adapter.DisposeAsync();

        await adapter.Reader.Completion.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(adapter.Reader.TryRead(out _));
    }

    [Fact]
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

    [Fact]
    public async Task QueueDisposal_ShouldCompleteChannel()
    {
        await using var queue = new TaskQueue<string>();
        var adapter = TaskQueueChannelAdapter<string>.Create(queue);

        await queue.DisposeAsync();

        await adapter.Reader.Completion.WaitAsync(TestContext.Current.CancellationToken);
        await adapter.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_WithOwnership_ShouldDisposeQueue()
    {
        var queue = new TaskQueue<string>();
        var adapter = TaskQueueChannelAdapter<string>.Create(queue, ownsQueue: true);

        await adapter.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await queue.EnqueueAsync("delta", TestContext.Current.CancellationToken));
    }
}
