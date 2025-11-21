using Hugo.TaskQueues;
using Hugo.TaskQueues.Replication;

using Microsoft.Extensions.Time.Testing;


namespace Hugo.UnitTests.TaskQueues;

public sealed class TaskQueueReplicationEventTests
{
    [Fact(Timeout = 15_000)]
    public void Create_ShouldIncrementSequenceAndStampTime()
    {
        var provider = new FakeTimeProvider();
        provider.SetUtcNow(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));

        TaskQueueLifecycleEvent<int> lifecycleEvent = new(
            EventId: 1,
            Kind: TaskQueueLifecycleEventKind.Enqueued,
            queue: "orders",
            SequenceId: 42,
            Attempt: 1,
            OccurredAt: provider.GetUtcNow(),
            EnqueuedAt: provider.GetUtcNow(),
            Value: 99,
            Error: null,
            OwnershipToken: null,
            LeaseExpiration: null,
            Flags: TaskQueueLifecycleEventMetadata.None);

        TaskQueueReplicationSourceOptions<int> options = new()
        {
            SourcePeerId = "source-a",
            DefaultOwnerPeerId = "worker-a",
            TimeProvider = provider
        };

        long sequence = 0;

        TaskQueueReplicationEvent<int> first = TaskQueueReplicationEvent<int>.Create(ref sequence, in lifecycleEvent, options, provider);

        first.SequenceNumber.ShouldBe(1);
        first.RecordedAt.ShouldBe(provider.GetUtcNow());

        provider.Advance(TimeSpan.FromSeconds(5));
        TaskQueueReplicationEvent<int> second = TaskQueueReplicationEvent<int>.Create(ref sequence, in lifecycleEvent, options, provider);

        second.SequenceNumber.ShouldBe(2);
        second.RecordedAt.ShouldBe(provider.GetUtcNow());
    }
}
