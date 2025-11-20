using Hugo.TaskQueues.Replication;

using Shouldly;

namespace Hugo.UnitTests.TaskQueues;

public sealed class TaskQueueReplicationCheckpointTests
{
    [Fact(Timeout = 15_000)]
    public void Advance_ShouldTrackPeersIndependently()
    {
        TaskQueueReplicationCheckpoint checkpoint = TaskQueueReplicationCheckpoint.Empty("stream");
        DateTimeOffset now = DateTimeOffset.UtcNow;

        checkpoint = checkpoint.Advance("peer-a", 5, now);

        checkpoint.GlobalPosition.ShouldBe(5);
        checkpoint.ShouldProcess("peer-a", 5).ShouldBeFalse();
        checkpoint.ShouldProcess("peer-a", 6).ShouldBeTrue();
        checkpoint.ShouldProcess("peer-b", 3).ShouldBeFalse();

        checkpoint = checkpoint.Advance("peer-b", 3, now.AddSeconds(1));

        checkpoint.TryGetPeerPosition("peer-b", out long peerB).ShouldBeTrue();
        peerB.ShouldBe(3);
        checkpoint.GlobalPosition.ShouldBe(5);

        checkpoint = checkpoint.Advance("peer-b", 12, now.AddSeconds(2));

        checkpoint.GlobalPosition.ShouldBe(12);
        checkpoint.ShouldProcess("peer-b", 11).ShouldBeFalse();
    }
}
