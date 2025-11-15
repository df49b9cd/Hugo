using Hugo;
using Hugo.TaskQueues;
using Hugo.TaskQueues.Replication;

using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests.TaskQueues;

[Collection("TaskQueueConcurrency")]
public sealed class TaskQueueReplicationFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async Task DeterministicCoordinator_ShouldPreventDuplicateSideEffectsOnReplay()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "feature.replication" }, provider);
        await using var source = new TaskQueueReplicationSource<int>(
            queue,
            new TaskQueueReplicationSourceOptions<int> { SourcePeerId = "feature-host", DefaultOwnerPeerId = "worker-a", TimeProvider = provider });

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

        await queue.EnqueueAsync(42, TestContext.Current.CancellationToken);
        TaskQueueLease<int> lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await lease.FailAsync(Error.From("transient", ErrorCodes.TaskQueueAbandoned), requeue: true, TestContext.Current.CancellationToken);
        TaskQueueLease<int> retry = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await retry.CompleteAsync(TestContext.Current.CancellationToken);

        List<TaskQueueReplicationEvent<int>> events = await reader;

        var checkpointStore = new InMemoryReplicationCheckpointStore();
        var deterministicStore = new InMemoryDeterministicStateStore();
        DeterministicEffectStore effectStore = new(
            deterministicStore,
            serializerOptions: TaskQueueReplicationJsonSerialization.CreateOptions<int>());
        var coordinator = new TaskQueueDeterministicCoordinator<int>(effectStore);

        var sink = new DeterministicRecordingSink("feature-stream", checkpointStore, coordinator, provider);
        await sink.ProcessAsync(ToAsyncEnumerable(events), TestContext.Current.CancellationToken);

        Assert.All(events, evt => Assert.Equal(1, sink.ExecutionCounts[evt.SequenceNumber]));

        // Simulate checkpoint loss but reuse deterministic store for replay safety.
        var replaySink = new DeterministicRecordingSink("feature-stream", new InMemoryReplicationCheckpointStore(), coordinator, provider);
        await replaySink.ProcessAsync(ToAsyncEnumerable(events), TestContext.Current.CancellationToken);

        Assert.Empty(replaySink.ExecutionCounts);
    }

    private static async IAsyncEnumerable<TaskQueueReplicationEvent<int>> ToAsyncEnumerable(IEnumerable<TaskQueueReplicationEvent<int>> events)
    {
        foreach (TaskQueueReplicationEvent<int> evt in events)
        {
            yield return evt;
            await Task.Yield();
        }
    }

    private sealed class DeterministicRecordingSink : CheckpointingTaskQueueReplicationSink<int>
    {
        private readonly TaskQueueDeterministicCoordinator<int> _coordinator;

        internal DeterministicRecordingSink(
            string streamId,
            ITaskQueueReplicationCheckpointStore store,
            TaskQueueDeterministicCoordinator<int> coordinator,
            TimeProvider timeProvider)
            : base(streamId, store, timeProvider)
        {
            _coordinator = coordinator;
        }

        internal Dictionary<long, int> ExecutionCounts { get; } = [];

        protected override async ValueTask ApplyEventAsync(TaskQueueReplicationEvent<int> replicationEvent, CancellationToken cancellationToken) => await _coordinator.ExecuteAsync(
                replicationEvent,
                (evt, _) =>
                {
                    ExecutionCounts.TryGetValue(evt.SequenceNumber, out int count);
                    ExecutionCounts[evt.SequenceNumber] = count + 1;
                    return ValueTask.FromResult(Result.Ok(evt.SequenceNumber));
                },
                cancellationToken).ConfigureAwait(false);
    }

    private sealed class InMemoryReplicationCheckpointStore : ITaskQueueReplicationCheckpointStore
    {
        private readonly Dictionary<string, TaskQueueReplicationCheckpoint> _checkpoints = new(StringComparer.OrdinalIgnoreCase);

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
            return ValueTask.CompletedTask;
        }
    }
}
