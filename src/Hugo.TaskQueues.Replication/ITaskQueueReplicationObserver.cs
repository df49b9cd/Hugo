using System.Threading;

namespace Hugo.TaskQueues.Replication;

/// <summary>
/// Observes replication events emitted from <see cref="TaskQueueReplicationSource{T}"/>.
/// </summary>
/// <typeparam name="T">Payload type handled by the queue.</typeparam>
public interface ITaskQueueReplicationObserver<T>
{
    /// <summary>
    /// Handles a replication event emitted by the source.
    /// </summary>
    /// <param name="replicationEvent">Replication event data.</param>
    /// <param name="cancellationToken">Cancellation token supplied by the source.</param>
    ValueTask OnReplicationEventAsync(TaskQueueReplicationEvent<T> replicationEvent, CancellationToken cancellationToken = default);
}
