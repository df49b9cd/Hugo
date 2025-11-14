namespace Hugo.TaskQueues.Replication;

/// <summary>
/// Base class for sinks that apply replication events with per-peer checkpoints.
/// </summary>
/// <typeparam name="T">Payload type handled by the queue.</typeparam>
public abstract class CheckpointingTaskQueueReplicationSink<T> : IAsyncDisposable
{
    private readonly string _streamId;
    private readonly ITaskQueueReplicationCheckpointStore _checkpointStore;
    private readonly TimeProvider _timeProvider;
    private TaskQueueReplicationCheckpoint _checkpoint;
    private int _initialized;
    private int _disposed;

    protected CheckpointingTaskQueueReplicationSink(
        string streamId,
        ITaskQueueReplicationCheckpointStore checkpointStore,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(streamId);
        _checkpointStore = checkpointStore ?? throw new ArgumentNullException(nameof(checkpointStore));
        _streamId = streamId;
        _checkpoint = TaskQueueReplicationCheckpoint.Empty(streamId);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Processes replication events from the source sequence.
    /// </summary>
    /// <param name="events">Replication events.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask ProcessAsync(IAsyncEnumerable<TaskQueueReplicationEvent<T>> events, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(events);
        await EnsureCheckpointAsync(cancellationToken).ConfigureAwait(false);

        await foreach (TaskQueueReplicationEvent<T> replicationEvent in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (Volatile.Read(ref _disposed) == 1)
            {
                break;
            }

            string peerKey = ResolvePeerKey(replicationEvent);
            if (!_checkpoint.ShouldProcess(peerKey, replicationEvent.SequenceNumber))
            {
                continue;
            }

            long sequenceLag = replicationEvent.SequenceNumber - _checkpoint.GlobalPosition;
            DateTimeOffset observedAt = _timeProvider.GetUtcNow();
            TimeSpan lagDuration = observedAt - replicationEvent.OccurredAt;
            GoDiagnostics.RecordTaskQueueReplicationLag(replicationEvent.QueueName, sequenceLag, lagDuration);

            await ApplyEventAsync(replicationEvent, cancellationToken).ConfigureAwait(false);

            _checkpoint = _checkpoint.Advance(peerKey, replicationEvent.SequenceNumber, observedAt);
            await _checkpointStore.PersistAsync(_checkpoint, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Applies an individual replication event.
    /// </summary>
    protected abstract ValueTask ApplyEventAsync(TaskQueueReplicationEvent<T> replicationEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Resolves the peer checkpoint key for the supplied event.
    /// </summary>
    protected virtual string ResolvePeerKey(TaskQueueReplicationEvent<T> replicationEvent) =>
        string.IsNullOrWhiteSpace(replicationEvent.OwnerPeerId) ? "default" : replicationEvent.OwnerPeerId!;

    private async ValueTask EnsureCheckpointAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _initialized) == 1)
        {
            return;
        }

        TaskQueueReplicationCheckpoint snapshot = await _checkpointStore.ReadAsync(_streamId, cancellationToken).ConfigureAwait(false);
        _checkpoint = snapshot ?? TaskQueueReplicationCheckpoint.Empty(_streamId);
        Volatile.Write(ref _initialized, 1);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
