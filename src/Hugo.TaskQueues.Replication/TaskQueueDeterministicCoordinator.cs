namespace Hugo.TaskQueues.Replication;

/// <summary>
/// Coordinates replication event handling using <see cref="DeterministicEffectStore"/>.
/// </summary>
/// <typeparam name="T">Payload type handled by the queue.</typeparam>
public sealed class TaskQueueDeterministicCoordinator<T>
{
    private readonly DeterministicEffectStore _effectStore;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskQueueDeterministicCoordinator{T}"/> class.
    /// </summary>
    /// <param name="effectStore">Deterministic effect store backing the coordinator.</param>
    public TaskQueueDeterministicCoordinator(DeterministicEffectStore effectStore)
    {
        _effectStore = effectStore ?? throw new ArgumentNullException(nameof(effectStore));
    }

    /// <summary>
    /// Captures a replication event as a deterministic effect to guarantee idempotent application.
    /// </summary>
    /// <param name="replicationEvent">Replication event.</param>
    /// <param name="handler">Handler that applies the event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <typeparam name="TResult">Result type returned by the handler.</typeparam>
    /// <returns>A deterministic result.</returns>
    public Task<Result<TResult>> ExecuteAsync<TResult>(
        TaskQueueReplicationEvent<T> replicationEvent,
        Func<TaskQueueReplicationEvent<T>, CancellationToken, Task<Result<TResult>>> handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handler);

        string effectId = CreateEffectId(replicationEvent);
        return _effectStore.CaptureAsync(effectId, ct => handler(replicationEvent, ct), cancellationToken);
    }

    /// <summary>
    /// Captures the replication event itself as an effect payload.
    /// </summary>
    /// <param name="replicationEvent">Replication event.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public Task<Result<TaskQueueReplicationEvent<T>>> CaptureEventAsync(
        TaskQueueReplicationEvent<T> replicationEvent,
        CancellationToken cancellationToken = default)
    {
        string effectId = CreateEffectId(replicationEvent);
        return _effectStore.CaptureAsync(effectId, _ => Task.FromResult(Result.Ok(replicationEvent)), cancellationToken);
    }

    private static string CreateEffectId(TaskQueueReplicationEvent<T> replicationEvent) =>
        $"taskqueue.replication::{replicationEvent.QueueName}::{replicationEvent.SequenceNumber}";
}
