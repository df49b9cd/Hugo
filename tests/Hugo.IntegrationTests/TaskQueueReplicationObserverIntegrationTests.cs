using System.Threading.Channels;

using Hugo.TaskQueues.Replication;

using Microsoft.Extensions.Time.Testing;


namespace Hugo.Tests;

[Collection("TaskQueueConcurrency")]
public sealed class TaskQueueReplicationObserverIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask RegisterObserver_ShouldStopEmittingAfterDisposal()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "dispatch", Capacity = 8 }, provider);
        await using var source = new TaskQueueReplicationSource<int>(queue, new TaskQueueReplicationSourceOptions<int>
        {
            SourcePeerId = "origin",
            TimeProvider = provider
        });

        var observer = new RecordingObserver<int>();
        using var subscription = source.RegisterObserver(observer);

        await queue.EnqueueAsync(1, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(2, TestContext.Current.CancellationToken);
        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await lease.CompleteAsync(TestContext.Current.CancellationToken);

        await observer.WaitForEventsAsync(3, TestContext.Current.CancellationToken);

        subscription.Dispose();

        await queue.EnqueueAsync(3, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(4, TestContext.Current.CancellationToken);

        var secondLease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await secondLease.CompleteAsync(TestContext.Current.CancellationToken);
        provider.Advance(TimeSpan.FromMilliseconds(1));

        await observer.AssertNoNewEventsAsync(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
    }

    private sealed class RecordingObserver<T> : ITaskQueueReplicationObserver<T>
    {
        private readonly Channel<TaskQueueReplicationEvent<T>> _channel = Channel.CreateUnbounded<TaskQueueReplicationEvent<T>>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        private int _totalEvents;

        public async ValueTask OnReplicationEventAsync(TaskQueueReplicationEvent<T> replicationEvent, CancellationToken cancellationToken = default)
        {
            await _channel.Writer.WriteAsync(replicationEvent, cancellationToken);
            Interlocked.Increment(ref _totalEvents);
        }

        public async ValueTask WaitForEventsAsync(int expectedCount, CancellationToken cancellationToken)
        {
            for (var i = 0; i < expectedCount; i++)
            {
                await _channel.Reader.ReadAsync(cancellationToken);
            }
        }

        public async ValueTask AssertNoNewEventsAsync(TimeSpan wait, CancellationToken cancellationToken)
        {
            var baseline = Volatile.Read(ref _totalEvents);
            await Task.Delay(wait, cancellationToken);
            Volatile.Read(ref _totalEvents).ShouldBe(baseline);
        }
    }
}
