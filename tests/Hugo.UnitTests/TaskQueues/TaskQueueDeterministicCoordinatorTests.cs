using Hugo.TaskQueues;
using Hugo.TaskQueues.Replication;


namespace Hugo.UnitTests.TaskQueues;

public sealed class TaskQueueDeterministicCoordinatorTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask ExecuteAsync_ShouldCaptureHandlerDeterministically()
    {
        var store = new InMemoryDeterministicStateStore();
        DeterministicEffectStore effectStore = new(
            store,
            serializerOptions: TaskQueueReplicationJsonSerialization.CreateOptions<int>(ReplicationTestJsonContext.Default));
        var coordinator = new TaskQueueDeterministicCoordinator<int>(effectStore);

        TaskQueueReplicationEvent<int> replicationEvent = new(
            SequenceNumber: 7,
            SourceEventId: 5,
            QueueName: "replication-tests",
            Kind: TaskQueueReplicationEventKind.Completed,
            SourcePeerId: "source-a",
            OwnerPeerId: "worker-a",
            OccurredAt: DateTimeOffset.UtcNow,
            RecordedAt: DateTimeOffset.UtcNow,
            TaskSequenceId: 11,
            Attempt: 2,
            Value: 42,
            Error: null,
            OwnershipToken: null,
            LeaseExpiration: null,
            Flags: TaskQueueLifecycleEventMetadata.None);

        int callCount = 0;

        Result<long> first = await coordinator.ExecuteAsync(
            replicationEvent,
            (evt, _) =>
            {
                callCount++;
                return ValueTask.FromResult(Result.Ok(evt.SequenceNumber));
            },
            TestContext.Current.CancellationToken);

        Result<long> second = await coordinator.ExecuteAsync(
            replicationEvent,
            (evt, _) =>
            {
                callCount++;
                return ValueTask.FromResult(Result.Ok(evt.SequenceNumber + 1));
            },
            TestContext.Current.CancellationToken);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        first.Value.ShouldBe(7);
        second.Value.ShouldBe(7);
        callCount.ShouldBe(1);
    }
}
