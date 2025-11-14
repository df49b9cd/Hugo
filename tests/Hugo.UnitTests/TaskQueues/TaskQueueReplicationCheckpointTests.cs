using Hugo.TaskQueues.Replication;

namespace Hugo.UnitTests.TaskQueues;

public sealed class TaskQueueReplicationCheckpointTests
{
    [Fact]
    public void Advance_ShouldTrackPeersIndependently()
    {
        TaskQueueReplicationCheckpoint checkpoint = TaskQueueReplicationCheckpoint.Empty("stream");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        checkpoint = checkpoint.Advance("peer-a", 5, now);

        Assert.Equal(5, checkpoint.GlobalPosition);
        Assert.False(checkpoint.ShouldProcess("peer-a", 5));
        Assert.True(checkpoint.ShouldProcess("peer-a", 6));
        Assert.False(checkpoint.ShouldProcess("peer-b", 3));

        checkpoint = checkpoint.Advance("peer-b", 3, now.AddSeconds(1));

        Assert.True(checkpoint.TryGetPeerPosition("peer-b", out long peerB));
        Assert.Equal(3, peerB);
        Assert.Equal(5, checkpoint.GlobalPosition);

        checkpoint = checkpoint.Advance("peer-b", 12, now.AddSeconds(2));

        Assert.Equal(12, checkpoint.GlobalPosition);
        Assert.False(checkpoint.ShouldProcess("peer-b", 11));
    }
}
