using Hugo.TaskQueues;
using Hugo.TaskQueues.Replication;
using Microsoft.Extensions.Time.Testing;

namespace Hugo.IntegrationTests;

[Collection("TaskQueueConcurrency")]
public sealed class TaskQueueReplicationIntegrationTests
{
    [Fact]
    public async Task ReplicationSource_ShouldEmitOrderedEvents()
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
            List<TaskQueueReplicationEvent<int>> events = new();
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

        await queue.EnqueueAsync(1);
        TaskQueueLease<int> lease = await queue.LeaseAsync();
        await lease.CompleteAsync();

        List<TaskQueueReplicationEvent<int>> captured = await reader.ConfigureAwait(false);

        TaskQueueReplicationEventKind[] kinds =
        [
            TaskQueueReplicationEventKind.Enqueued,
            TaskQueueReplicationEventKind.LeaseGranted,
            TaskQueueReplicationEventKind.Completed
        ];

        Assert.Equal(kinds, captured.Select(e => e.Kind));
        Assert.Equal(new long[] { 1, 2, 3 }, captured.Select(e => e.SequenceNumber));
        Assert.All(captured, evt => Assert.Equal("replication.integration", evt.QueueName));
    }

    [Fact]
    public async Task CheckpointingSink_ShouldResumeFromPersistedCheckpoint()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "replication.checkpoints", Capacity = 8 }, provider);
        await using var source = new TaskQueueReplicationSource<int>(
            queue,
            new TaskQueueReplicationSourceOptions<int> { SourcePeerId = "origin", TimeProvider = provider });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<List<TaskQueueReplicationEvent<int>>> reader = Task.Run(async () =>
        {
            List<TaskQueueReplicationEvent<int>> events = new();
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
            await queue.EnqueueAsync(i);
            TaskQueueLease<int> lease = await queue.LeaseAsync();
            await lease.CompleteAsync();
        }

        List<TaskQueueReplicationEvent<int>> events = await reader.ConfigureAwait(false);

        var store = new InMemoryReplicationCheckpointStore();
        var firstSink = new RecordingReplicationSink("stream", store, provider);
        await firstSink.ProcessAsync(ToAsyncEnumerable(events.Take(3))).ConfigureAwait(false);

        Assert.Equal(new long[] { 1, 2, 3 }, firstSink.Processed);

        var resumedSink = new RecordingReplicationSink("stream", store, provider);
        await resumedSink.ProcessAsync(ToAsyncEnumerable(events)).ConfigureAwait(false);

        Assert.Equal(new long[] { 4, 5, 6 }, resumedSink.Processed);
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

        internal List<long> Processed { get; } = new();

        protected override ValueTask ApplyEventAsync(TaskQueueReplicationEvent<int> replicationEvent, CancellationToken cancellationToken)
        {
            Processed.Add(replicationEvent.SequenceNumber);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryReplicationCheckpointStore : ITaskQueueReplicationCheckpointStore
    {
        private readonly Dictionary<string, TaskQueueReplicationCheckpoint> _checkpoints = new(StringComparer.OrdinalIgnoreCase);

        public ValueTask<TaskQueueReplicationCheckpoint> ReadAsync(string streamId, CancellationToken cancellationToken = default)
        {
            if (_checkpoints.TryGetValue(streamId, out TaskQueueReplicationCheckpoint checkpoint))
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
