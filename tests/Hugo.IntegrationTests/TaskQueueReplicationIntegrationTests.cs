using Hugo.TaskQueues.Replication;
using Shouldly;

using Microsoft.Extensions.Time.Testing;

namespace Hugo.IntegrationTests;

[Collection("TaskQueueConcurrency")]
public sealed class TaskQueueReplicationIntegrationTests
{
    [Fact(Timeout = 15_000)]
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
