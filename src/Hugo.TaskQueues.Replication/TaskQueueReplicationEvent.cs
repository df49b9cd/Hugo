using System.Threading;

using Hugo.TaskQueues;

namespace Hugo.TaskQueues.Replication;

/// <summary>
/// Identifies the operation represented by a replication event.
/// </summary>
public enum TaskQueueReplicationEventKind
{
    Enqueued,
    LeaseGranted,
    Heartbeat,
    Completed,
    Failed,
    Requeued,
    DeadLettered,
    LeaseExpired,
    Drained
}

/// <summary>
/// Represents a serialized queue mutation emitted from <see cref="TaskQueueReplicationSource{T}"/>.
/// </summary>
/// <typeparam name="T">Payload type handled by the queue.</typeparam>
public sealed record class TaskQueueReplicationEvent<T>(
    long SequenceNumber,
    long SourceEventId,
    string QueueName,
    TaskQueueReplicationEventKind Kind,
    string? SourcePeerId,
    string? OwnerPeerId,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    long TaskSequenceId,
    int Attempt,
    T? Value,
    Error? Error,
    TaskQueueOwnershipToken? OwnershipToken,
    DateTimeOffset? LeaseExpiration,
    TaskQueueLifecycleEventMetadata Flags)
{
    internal static TaskQueueReplicationEvent<T> Create(
        ref long sequenceCounter,
        in TaskQueueLifecycleEvent<T> lifecycleEvent,
        TaskQueueReplicationSourceOptions<T> options,
        TimeProvider timeProvider)
    {
        long sequenceNumber = Interlocked.Increment(ref sequenceCounter);
        if (sequenceNumber <= 0)
        {
            throw new InvalidOperationException("Replication sequence number overflowed.");
        }

        DateTimeOffset recordedAt = (options.TimeProvider ?? timeProvider).GetUtcNow();
        TaskQueueReplicationEventKind kind = MapKind(lifecycleEvent.Kind);
        string? ownerPeer = options.OwnerPeerResolver?.Invoke(lifecycleEvent) ?? options.DefaultOwnerPeerId;

        return new TaskQueueReplicationEvent<T>(
            sequenceNumber,
            lifecycleEvent.EventId,
            lifecycleEvent.QueueName,
            kind,
            options.SourcePeerId,
            ownerPeer,
            lifecycleEvent.OccurredAt,
            recordedAt,
            lifecycleEvent.SequenceId,
            lifecycleEvent.Attempt,
            lifecycleEvent.Value,
            lifecycleEvent.Error,
            lifecycleEvent.OwnershipToken,
            lifecycleEvent.LeaseExpiration,
            lifecycleEvent.Flags);
    }

    private static TaskQueueReplicationEventKind MapKind(TaskQueueLifecycleEventKind kind) =>
        kind switch
        {
            TaskQueueLifecycleEventKind.Enqueued => TaskQueueReplicationEventKind.Enqueued,
            TaskQueueLifecycleEventKind.LeaseGranted => TaskQueueReplicationEventKind.LeaseGranted,
            TaskQueueLifecycleEventKind.Heartbeat => TaskQueueReplicationEventKind.Heartbeat,
            TaskQueueLifecycleEventKind.Completed => TaskQueueReplicationEventKind.Completed,
            TaskQueueLifecycleEventKind.Failed => TaskQueueReplicationEventKind.Failed,
            TaskQueueLifecycleEventKind.Requeued => TaskQueueReplicationEventKind.Requeued,
            TaskQueueLifecycleEventKind.DeadLettered => TaskQueueReplicationEventKind.DeadLettered,
            TaskQueueLifecycleEventKind.Expired => TaskQueueReplicationEventKind.LeaseExpired,
            TaskQueueLifecycleEventKind.Drained => TaskQueueReplicationEventKind.Drained,
            _ => TaskQueueReplicationEventKind.Enqueued
        };
}
