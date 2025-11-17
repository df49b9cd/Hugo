using System.Collections.Generic;

using Hugo;
using Hugo.TaskQueues;
using Hugo.TaskQueues.Replication;
using Microsoft.Extensions.Time.Testing;
using Shouldly;

namespace Hugo.UnitTests.TaskQueues;

public sealed class CheckpointingTaskQueueReplicationSinkTests
{
    [Fact(Timeout = 15_000)]
    public async Task ProcessAsync_ShouldAdvanceAndPersistCheckpointPerPeer()
    {
        var timeProvider = new FakeTimeProvider();
        var checkpointStore = new RecordingCheckpointStore(TaskQueueReplicationCheckpoint.Empty("stream-1"));
        var sink = new TestReplicationSink("stream-1", checkpointStore, timeProvider);

        TaskQueueReplicationEvent<int>[] events =
        [
            new(
                SequenceNumber: 1,
                SourceEventId: 1,
                QueueName: "widgets",
                Kind: TaskQueueReplicationEventKind.Enqueued,
                SourcePeerId: "source-a",
                OwnerPeerId: "peer-a",
                OccurredAt: timeProvider.GetUtcNow(),
                RecordedAt: timeProvider.GetUtcNow(),
                TaskSequenceId: 1,
                Attempt: 1,
                Value: 11,
                Error: null,
                OwnershipToken: null,
                LeaseExpiration: null,
                Flags: TaskQueueLifecycleEventMetadata.None),
            new(
                SequenceNumber: 2,
                SourceEventId: 2,
                QueueName: "widgets",
                Kind: TaskQueueReplicationEventKind.Completed,
                SourcePeerId: "source-a",
                OwnerPeerId: null,
                OccurredAt: timeProvider.GetUtcNow(),
                RecordedAt: timeProvider.GetUtcNow(),
                TaskSequenceId: 2,
                Attempt: 1,
                Value: 22,
                Error: null,
                OwnershipToken: null,
                LeaseExpiration: null,
                Flags: TaskQueueLifecycleEventMetadata.None)
        ];

        await sink.ProcessAsync(ToAsync(events), CancellationToken.None);

        sink.AppliedEvents.Count.ShouldBe(2);
        checkpointStore.ReadCount.ShouldBe(1);

        checkpointStore.PersistedCheckpoint.ShouldNotBeNull();
        checkpointStore.PersistedCheckpoint!.GlobalPosition.ShouldBe(2);
        checkpointStore.PersistedCheckpoint.TryGetPeerPosition("peer-a", out long peerAPosition).ShouldBeTrue();
        peerAPosition.ShouldBe(1);
        checkpointStore.PersistedCheckpoint.TryGetPeerPosition("default", out long defaultPeer).ShouldBeTrue();
        defaultPeer.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async Task ProcessAsync_ShouldSkipEventsAtOrBelowCheckpoint()
    {
        var timeProvider = new FakeTimeProvider();
        var existing = new TaskQueueReplicationCheckpoint(
            "stream-1",
            GlobalPosition: 3,
            UpdatedAt: timeProvider.GetUtcNow(),
            PeerPositions: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["peer-a"] = 3
            });

        var checkpointStore = new RecordingCheckpointStore(existing);
        var sink = new TestReplicationSink("stream-1", checkpointStore, timeProvider);

        TaskQueueReplicationEvent<int>[] events =
        [
            new(
                SequenceNumber: 2,
                SourceEventId: 10,
                QueueName: "widgets",
                Kind: TaskQueueReplicationEventKind.Heartbeat,
                SourcePeerId: "source-a",
                OwnerPeerId: "peer-a",
                OccurredAt: timeProvider.GetUtcNow(),
                RecordedAt: timeProvider.GetUtcNow(),
                TaskSequenceId: 2,
                Attempt: 1,
                Value: 5,
                Error: null,
                OwnershipToken: null,
                LeaseExpiration: null,
                Flags: TaskQueueLifecycleEventMetadata.None),
            new(
                SequenceNumber: 4,
                SourceEventId: 11,
                QueueName: "widgets",
                Kind: TaskQueueReplicationEventKind.Requeued,
                SourcePeerId: "source-a",
                OwnerPeerId: "peer-a",
                OccurredAt: timeProvider.GetUtcNow(),
                RecordedAt: timeProvider.GetUtcNow(),
                TaskSequenceId: 4,
                Attempt: 2,
                Value: 9,
                Error: Error.From("late retry", "taskqueue.retry"),
                OwnershipToken: null,
                LeaseExpiration: null,
                Flags: TaskQueueLifecycleEventMetadata.None)
        ];

        await sink.ProcessAsync(ToAsync(events), CancellationToken.None);

        sink.AppliedEvents.Count.ShouldBe(1);
        sink.AppliedEvents.Single().SequenceNumber.ShouldBe(4);

        checkpointStore.PersistedCheckpoint.ShouldNotBeNull();
        checkpointStore.PersistedCheckpoint!.GlobalPosition.ShouldBe(4);
        checkpointStore.PersistedCheckpoint.TryGetPeerPosition("peer-a", out long peerPosition).ShouldBeTrue();
        peerPosition.ShouldBe(4);
    }

    private static async IAsyncEnumerable<TaskQueueReplicationEvent<int>> ToAsync(IEnumerable<TaskQueueReplicationEvent<int>> events)
    {
        foreach (TaskQueueReplicationEvent<int> replicationEvent in events)
        {
            yield return replicationEvent;
            await Task.Yield();
        }
    }

    private sealed class TestReplicationSink : CheckpointingTaskQueueReplicationSink<int>
    {
        public TestReplicationSink(string streamId, ITaskQueueReplicationCheckpointStore checkpointStore, TimeProvider timeProvider)
            : base(streamId, checkpointStore, timeProvider)
        {
        }

        public List<TaskQueueReplicationEvent<int>> AppliedEvents { get; } = [];

        protected override ValueTask ApplyEventAsync(TaskQueueReplicationEvent<int> replicationEvent, CancellationToken cancellationToken)
        {
            AppliedEvents.Add(replicationEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingCheckpointStore : ITaskQueueReplicationCheckpointStore
    {
        public RecordingCheckpointStore(TaskQueueReplicationCheckpoint snapshot)
        {
            Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        }

        public TaskQueueReplicationCheckpoint Snapshot { get; }

        public TaskQueueReplicationCheckpoint? PersistedCheckpoint { get; private set; }

        public int ReadCount { get; private set; }

        public ValueTask<TaskQueueReplicationCheckpoint> ReadAsync(string streamId, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromResult(Snapshot);
        }

        public ValueTask PersistAsync(TaskQueueReplicationCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            PersistedCheckpoint = checkpoint ?? throw new ArgumentNullException(nameof(checkpoint));
            return ValueTask.CompletedTask;
        }
    }
}
