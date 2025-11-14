namespace Hugo.TaskQueues.Replication;

/// <summary>
/// Provides persistence for replication checkpoints.
/// </summary>
public interface ITaskQueueReplicationCheckpointStore
{
    /// <summary>
    /// Reads the last checkpoint for the supplied stream.
    /// </summary>
    ValueTask<TaskQueueReplicationCheckpoint> ReadAsync(string streamId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists the supplied checkpoint.
    /// </summary>
    ValueTask PersistAsync(TaskQueueReplicationCheckpoint checkpoint, CancellationToken cancellationToken = default);
}
