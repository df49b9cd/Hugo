using Hugo.TaskQueues;

namespace Hugo.TaskQueues.Replication;

/// <summary>
/// Configures a <see cref="TaskQueueReplicationSource{T}"/>.
/// </summary>
/// <typeparam name="T">Payload type handled by the queue.</typeparam>
public sealed class TaskQueueReplicationSourceOptions<T>
{
    /// <summary>
    /// Gets or sets the peer identifier for the source emitting replication events.
    /// </summary>
    public string? SourcePeerId { get; init; }

    /// <summary>
    /// Gets or sets the default owner peer identifier used when <see cref="OwnerPeerResolver"/> does not return a value.
    /// </summary>
    public string? DefaultOwnerPeerId { get; init; }

    /// <summary>
    /// Gets or sets a resolver that extracts the owner peer identifier from lifecycle events.
    /// </summary>
    public Func<TaskQueueLifecycleEvent<T>, string?>? OwnerPeerResolver { get; init; }

    /// <summary>
    /// Gets or sets the time provider used to timestamp emitted replication events.
    /// </summary>
    public TimeProvider? TimeProvider { get; init; }
}
