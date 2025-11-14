using System.Diagnostics.CodeAnalysis;

namespace Hugo.TaskQueues;

/// <summary>
/// Describes the lifecycle operation observed on a <see cref="Hugo.TaskQueue{T}"/>.
/// </summary>
public enum TaskQueueLifecycleEventKind
{
    Enqueued,
    LeaseGranted,
    Heartbeat,
    Completed,
    Failed,
    Requeued,
    DeadLettered,
    Expired,
    Drained
}

/// <summary>
/// Provides additional metadata describing the source of a lifecycle event.
/// </summary>
[Flags]
public enum TaskQueueLifecycleEventMetadata
{
    None = 0,
    FromDrain = 1,
    FromExpiration = 2,
    DeadLetter = 4
}

/// <summary>
/// Represents a lifecycle event emitted by <see cref="Hugo.TaskQueue{T}"/>.
/// </summary>
/// <typeparam name="T">Payload type handled by the queue.</typeparam>
public readonly record struct TaskQueueLifecycleEvent<T>(
    long EventId,
    TaskQueueLifecycleEventKind Kind,
    string queue,
    long SequenceId,
    int Attempt,
    DateTimeOffset OccurredAt,
    DateTimeOffset EnqueuedAt,
    T? Value,
    Error? Error,
    TaskQueueOwnershipToken? OwnershipToken,
    DateTimeOffset? LeaseExpiration,
    TaskQueueLifecycleEventMetadata Flags)
{
    /// <summary>
    /// Gets the queue name recorded with the event.
    /// </summary>
    public string QueueName { get; } = queue ?? throw new ArgumentNullException(nameof(queue));
}

/// <summary>
/// Receives lifecycle events emitted from <see cref="Hugo.TaskQueue{T}"/>.
/// </summary>
/// <typeparam name="T">Payload type handled by the queue.</typeparam>
public interface ITaskQueueLifecycleListener<T>
{
    /// <summary>
    /// Handles a lifecycle event raised by the queue.
    /// </summary>
    /// <param name="lifecycleEvent">Lifecycle event context.</param>
    [SuppressMessage("Design", "CA1030:Use events where appropriate", Justification = "Consumers require synchronous delivery without delegate allocations.")]
    void OnEvent(in TaskQueueLifecycleEvent<T> lifecycleEvent);
}
