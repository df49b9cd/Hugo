using System.Diagnostics;
using System.Linq;
using System.Threading.Channels;

using Hugo.TaskQueues;
using Hugo.TaskQueues.Replication;

using Microsoft.Extensions.Time.Testing;


namespace Hugo.Tests.TaskQueues;

public class TaskQueueReplicationSourceTests
{
    [Fact(Timeout = 15_000)]
    public async Task OnEvent_ShouldStreamEventsAndNotifyObservers()
    {
        GoDiagnostics.Reset();

        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "rep-source", Capacity = 4 }, provider);
        await using var source = new TaskQueueReplicationSource<int>(queue, new TaskQueueReplicationSourceOptions<int>
        {
            SourcePeerId = "origin",
            DefaultOwnerPeerId = "owner-a",
            TimeProvider = provider
        });

        var recording = new DelegatingObserver<int>(_ => ValueTask.CompletedTask);
        var failing = new DelegatingObserver<int>(_ => new ValueTask(Task.Run(() => throw new InvalidOperationException("boom"))));

        using var subscription = source.RegisterObserver(recording);
        using var failingSubscription = source.RegisterObserver(failing);

        var listener = (ITaskQueueLifecycleListener<int>)source;
        DateTimeOffset occurred = provider.GetUtcNow();
        var lifecycle = new TaskQueueLifecycleEvent<int>(
            EventId: 1,
            Kind: TaskQueueLifecycleEventKind.Enqueued,
            queue: queue.QueueName,
            SequenceId: 7,
            Attempt: 1,
            OccurredAt: occurred,
            EnqueuedAt: occurred,
            Value: 123,
            Error: null,
            OwnershipToken: null,
            LeaseExpiration: null,
            Flags: TaskQueueLifecycleEventMetadata.None);

        listener.OnEvent(in lifecycle);

        TaskQueueReplicationEvent<int> replicationEvent = await source.Reader.ReadAsync(TestContext.Current.CancellationToken);

        // Allow the asynchronous observer path to run and swallow its exception.
        await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);

        replicationEvent.QueueName.ShouldBe(queue.QueueName);
        replicationEvent.OwnerPeerId.ShouldBe("owner-a");
        recording.Events.ShouldHaveSingleItem();
        recording.Events[0].SequenceNumber.ShouldBe(replicationEvent.SequenceNumber);
    }

    [Fact(Timeout = 15_000)]
    public async Task PumpToAsync_ShouldForwardEventsAndCompleteOnDispose()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "rep-source-pump", Capacity = 4 }, provider);
        await using var source = new TaskQueueReplicationSource<int>(queue, new TaskQueueReplicationSourceOptions<int>
        {
            SourcePeerId = "origin",
            TimeProvider = provider
        });

        Channel<TaskQueueReplicationEvent<int>> forwardChannel = Channel.CreateUnbounded<TaskQueueReplicationEvent<int>>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        ValueTask pumpTask = source.PumpToAsync(forwardChannel.Writer, cts.Token);

        var listener = (ITaskQueueLifecycleListener<int>)source;
        DateTimeOffset now = provider.GetUtcNow();

        var first = new TaskQueueLifecycleEvent<int>(
            EventId: 1,
            Kind: TaskQueueLifecycleEventKind.Enqueued,
            queue: queue.QueueName,
            SequenceId: 1,
            Attempt: 1,
            OccurredAt: now,
            EnqueuedAt: now,
            Value: 1,
            Error: null,
            OwnershipToken: null,
            LeaseExpiration: null,
            Flags: TaskQueueLifecycleEventMetadata.None);

        listener.OnEvent(in first);

        provider.Advance(TimeSpan.FromMilliseconds(1));
        now = provider.GetUtcNow();

        var second = new TaskQueueLifecycleEvent<int>(
            EventId: 2,
            Kind: TaskQueueLifecycleEventKind.Completed,
            queue: queue.QueueName,
            SequenceId: 2,
            Attempt: 1,
            OccurredAt: now,
            EnqueuedAt: now,
            Value: 1,
            Error: null,
            OwnershipToken: null,
            LeaseExpiration: null,
            Flags: TaskQueueLifecycleEventMetadata.None);

        listener.OnEvent(in second);

        List<TaskQueueReplicationEvent<int>> forwarded = [];
        forwarded.Add(await forwardChannel.Reader.ReadAsync(cts.Token));
        forwarded.Add(await forwardChannel.Reader.ReadAsync(cts.Token));

        forwarded.Select(e => e.SequenceNumber).ShouldBe(new long[] { 1, 2 });

        await source.DisposeAsync();
        await pumpTask.AsTask().WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        pumpTask.IsCompletedSuccessfully.ShouldBeTrue();
    }

    private sealed class DelegatingObserver<T> : ITaskQueueReplicationObserver<T>
    {
        private readonly Func<TaskQueueReplicationEvent<T>, ValueTask> _invocation;

        internal DelegatingObserver(Func<TaskQueueReplicationEvent<T>, ValueTask> invocation)
        {
            _invocation = invocation;
        }

        internal List<TaskQueueReplicationEvent<T>> Events { get; } = [];

        public ValueTask OnReplicationEventAsync(TaskQueueReplicationEvent<T> replicationEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(replicationEvent);
            return _invocation(replicationEvent);
        }
    }
}
