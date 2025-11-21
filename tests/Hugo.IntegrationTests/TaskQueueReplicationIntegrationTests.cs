using Hugo.TaskQueues;
using Hugo.TaskQueues.Replication;

using Microsoft.Extensions.Time.Testing;


namespace Hugo.IntegrationTests;

[Collection("TaskQueueConcurrency")]
public sealed class TaskQueueReplicationIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask ReplicationSource_ShouldEmitOrderedEvents()
    {
        var provider = new FakeTimeProvider();
        TaskQueueOptions options = new() { Name = "replication.integration", Capacity = 8 };

        await using var queue = new TaskQueue<int>(options, provider);
        await using var source = new TaskQueueReplicationSource<int>(
            queue,
            new TaskQueueReplicationSourceOptions<int> { SourcePeerId = "origin", TimeProvider = provider });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<List<TaskQueueReplicationEvent<int>>> reader = Task.Run(async () =>
        {
            List<TaskQueueReplicationEvent<int>> events = [];
            await foreach (TaskQueueReplicationEvent<int> evt in source.ReadEventsAsync(cts.Token))
            {
                events.Add(evt);
                if (events.Count == 3)
                {
                    break;
                }
            }

            return events;
        });

        await queue.EnqueueAsync(1, TestContext.Current.CancellationToken);
        TaskQueueLease<int> lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await lease.CompleteAsync(TestContext.Current.CancellationToken);

        List<TaskQueueReplicationEvent<int>> captured = await reader;

        TaskQueueReplicationEventKind[] kinds =
        [
            TaskQueueReplicationEventKind.Enqueued,
            TaskQueueReplicationEventKind.LeaseGranted,
            TaskQueueReplicationEventKind.Completed
        ];

        captured.Select(e => e.Kind).ShouldBe(kinds);
        captured.Select(e => e.SequenceNumber).ShouldBe([1, 2, 3]);
        captured.ShouldAllBe(evt => evt.QueueName == "replication.integration");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CheckpointingSink_ShouldResumeFromPersistedCheckpoint()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "replication.checkpoints", Capacity = 8 }, provider);
        await using var source = new TaskQueueReplicationSource<int>(
            queue,
            new TaskQueueReplicationSourceOptions<int> { SourcePeerId = "origin", TimeProvider = provider });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<List<TaskQueueReplicationEvent<int>>> reader = Task.Run(async () =>
        {
            List<TaskQueueReplicationEvent<int>> events = [];
            await foreach (TaskQueueReplicationEvent<int> evt in source.ReadEventsAsync(cts.Token))
            {
                events.Add(evt);
                if (events.Count == 6)
                {
                    break;
                }
            }

            return events;
        });

        for (int i = 0; i < 2; i++)
        {
            await queue.EnqueueAsync(i, TestContext.Current.CancellationToken);
            TaskQueueLease<int> lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
            await lease.CompleteAsync(TestContext.Current.CancellationToken);
        }

        List<TaskQueueReplicationEvent<int>> events = await reader;

        var store = new InMemoryReplicationCheckpointStore();
        var firstSink = new RecordingReplicationSink("stream", store, provider);
        await firstSink.ProcessAsync(ToAsyncEnumerable(events.Take(3)), TestContext.Current.CancellationToken);

        firstSink.Processed.ShouldBe(new long[] { 1, 2, 3 });

        var resumedSink = new RecordingReplicationSink("stream", store, provider);
        await resumedSink.ProcessAsync(ToAsyncEnumerable(events), TestContext.Current.CancellationToken);

        resumedSink.Processed.ShouldBe(new long[] { 4, 5, 6 });
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CheckpointingSink_ShouldSkipAlreadyProcessedSequences_PerPeer()
    {
        var provider = new FakeTimeProvider();

        var initialCheckpoint = new TaskQueueReplicationCheckpoint(
            "replication.dedup",
            GlobalPosition: 5,
            UpdatedAt: provider.GetUtcNow(),
            PeerPositions: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
            {
                ["peer-a"] = 5,
                ["peer-b"] = 3
            });

        var store = new InMemoryReplicationCheckpointStore(initialCheckpoint);
        var sink = new RecordingReplicationSink("replication.dedup", store, provider);

        List<TaskQueueReplicationEvent<int>> events =
        [
            new TaskQueueReplicationEvent<int>(
                4,
                1,
                "queue",
                TaskQueueReplicationEventKind.Enqueued,
                "origin",
                "peer-a",
                provider.GetUtcNow(),
                provider.GetUtcNow(),
                1,
                1,
                42,
                null,
                null,
                null,
                TaskQueueLifecycleEventMetadata.None),
            new TaskQueueReplicationEvent<int>(
                6,
                2,
                "queue",
                TaskQueueReplicationEventKind.Completed,
                "origin",
                "peer-a",
                provider.GetUtcNow(),
                provider.GetUtcNow(),
                2,
                1,
                43,
                null,
                null,
                null,
                TaskQueueLifecycleEventMetadata.None),
            new TaskQueueReplicationEvent<int>(
                7,
                3,
                "queue",
                TaskQueueReplicationEventKind.Enqueued,
                "origin",
                "peer-c",
                provider.GetUtcNow(),
                provider.GetUtcNow(),
                3,
                1,
                44,
                null,
                null,
                null,
                TaskQueueLifecycleEventMetadata.None)
        ];

        await sink.ProcessAsync(ToAsyncEnumerable(events), TestContext.Current.CancellationToken);

        sink.Processed.ShouldBe(new long[] { 6, 7 });
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CheckpointingSink_ShouldPersistDefaultPeerKey_WhenOwnerMissing()
    {
        var provider = new FakeTimeProvider();

        var store = new InMemoryReplicationCheckpointStore();
        var sink = new RecordingReplicationSink("replication.defaults", store, provider);

        DateTimeOffset occurred = provider.GetUtcNow();

        TaskQueueReplicationEvent<int>[] events =
        [
            new TaskQueueReplicationEvent<int>(
                1,
                101,
                "queue-defaults",
                TaskQueueReplicationEventKind.Enqueued,
                "source",
                null,
                occurred,
                occurred,
                10,
                1,
                11,
                null,
                null,
                null,
                TaskQueueLifecycleEventMetadata.None),
            new TaskQueueReplicationEvent<int>(
                2,
                102,
                "queue-defaults",
                TaskQueueReplicationEventKind.Heartbeat,
                "source",
                string.Empty,
                provider.GetUtcNow(),
                provider.GetUtcNow(),
                11,
                1,
                12,
                null,
                null,
                null,
                TaskQueueLifecycleEventMetadata.None),
            new TaskQueueReplicationEvent<int>(
                3,
                103,
                "queue-defaults",
                TaskQueueReplicationEventKind.Completed,
                "source",
                "peer-x",
                provider.GetUtcNow(),
                provider.GetUtcNow(),
                12,
                1,
                13,
                null,
                null,
                null,
                TaskQueueLifecycleEventMetadata.None)
        ];

        await sink.ProcessAsync(ToAsyncEnumerable(events), TestContext.Current.CancellationToken);
        await sink.DisposeAsync();

        sink.Processed.ShouldBe(new long[] { 1, 2, 3 });

        store.LastPersisted.ShouldNotBeNull();
        store.LastPersisted!.PeerPositions["default"].ShouldBe(2);
        store.LastPersisted!.PeerPositions["peer-x"].ShouldBe(3);
        store.LastPersisted!.GlobalPosition.ShouldBe(3);
    }

    private static async IAsyncEnumerable<TaskQueueReplicationEvent<int>> ToAsyncEnumerable(IEnumerable<TaskQueueReplicationEvent<int>> events)
    {
        foreach (TaskQueueReplicationEvent<int> evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed class RecordingReplicationSink : CheckpointingTaskQueueReplicationSink<int>
    {
        internal RecordingReplicationSink(string streamId, ITaskQueueReplicationCheckpointStore store, TimeProvider timeProvider)
            : base(streamId, store, timeProvider)
        {
        }

        internal List<long> Processed { get; } = [];

        protected override ValueTask ApplyEventAsync(TaskQueueReplicationEvent<int> replicationEvent, CancellationToken cancellationToken)
        {
            Processed.Add(replicationEvent.SequenceNumber);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryReplicationCheckpointStore : ITaskQueueReplicationCheckpointStore
    {
        private readonly Dictionary<string, TaskQueueReplicationCheckpoint> _checkpoints = new(StringComparer.OrdinalIgnoreCase);
        private TaskQueueReplicationCheckpoint? _lastPersisted;

        internal TaskQueueReplicationCheckpoint? LastPersisted => _lastPersisted;

        internal InMemoryReplicationCheckpointStore(TaskQueueReplicationCheckpoint? initial = null)
        {
            if (initial is not null)
            {
                _checkpoints[initial.StreamId] = initial;
            }
        }

        public ValueTask<TaskQueueReplicationCheckpoint> ReadAsync(string streamId, CancellationToken cancellationToken = default)
        {
            if (_checkpoints.TryGetValue(streamId, out TaskQueueReplicationCheckpoint? checkpoint) && checkpoint is not null)
            {
                return ValueTask.FromResult(checkpoint);
            }

            return ValueTask.FromResult(TaskQueueReplicationCheckpoint.Empty(streamId));
        }

        public ValueTask PersistAsync(TaskQueueReplicationCheckpoint checkpoint, CancellationToken cancellationToken = default)
        {
            _checkpoints[checkpoint.StreamId] = checkpoint;
            _lastPersisted = checkpoint;
            return ValueTask.CompletedTask;
        }
    }
}
