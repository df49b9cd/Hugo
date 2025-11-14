namespace Hugo.TaskQueues.Replication;

/// <summary>
/// Represents persisted replication progress for a stream.
/// </summary>
public sealed record class TaskQueueReplicationCheckpoint(
    string StreamId,
    long GlobalPosition,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, long> PeerPositions)
{
    /// <summary>
    /// Creates an empty checkpoint for the specified stream.
    /// </summary>
    public static TaskQueueReplicationCheckpoint Empty(string streamId) =>
        new(streamId, 0, DateTimeOffset.MinValue, new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Determines whether the supplied event should be processed for the given peer.
    /// </summary>
    public bool ShouldProcess(string peerId, long sequenceNumber)
    {
        if (sequenceNumber <= GlobalPosition)
        {
            return false;
        }

        if (PeerPositions.TryGetValue(peerId, out long peerPosition))
        {
            return sequenceNumber > peerPosition;
        }

        return true;
    }

    /// <summary>
    /// Advances the checkpoint for the supplied peer.
    /// </summary>
    public TaskQueueReplicationCheckpoint Advance(string peerId, long sequenceNumber, DateTimeOffset observedAt)
    {
        Dictionary<string, long> peers = new(PeerPositions, StringComparer.OrdinalIgnoreCase)
        {
            [peerId] = sequenceNumber
        };

        long global = Math.Max(sequenceNumber, GlobalPosition);
        return new TaskQueueReplicationCheckpoint(StreamId, global, observedAt, peers);
    }

    /// <summary>
    /// Attempts to get the checkpoint recorded for a specific peer.
    /// </summary>
    public bool TryGetPeerPosition(string peerId, out long position) =>
        PeerPositions.TryGetValue(peerId, out position);
}
