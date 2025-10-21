using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests;

public class VersionGateTests
{
    [Fact]
    public void Require_ShouldRecordMaxVersion_WhenMissing()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var decision = gate.Require("feature.step1", VersionGate.DefaultVersion, 2);

        Assert.True(decision.IsSuccess);
        Assert.True(decision.Value.IsNew);
        Assert.Equal(2, decision.Value.Version);
    }

    [Fact]
    public void Require_ShouldReturnPersistedVersion_OnSubsequentInvocations()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var first = gate.Require("feature.step2", VersionGate.DefaultVersion, 3);
        var second = gate.Require("feature.step2", VersionGate.DefaultVersion, 3);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.True(first.Value.IsNew);
        Assert.False(second.Value.IsNew);
        Assert.Equal(first.Value.Version, second.Value.Version);
    }

    [Fact]
    public void Require_ShouldFail_WhenPersistedVersionOutsideRange()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var initial = gate.Require("feature.step3", VersionGate.DefaultVersion, 5);
        Assert.True(initial.IsSuccess);

        var replay = gate.Require("feature.step3", 6, 7);

        Assert.True(replay.IsFailure);
        Assert.Equal(ErrorCodes.VersionConflict, replay.Error?.Code);
    }

    [Fact]
    public void Require_ShouldUseInitialVersionProvider()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var decision = gate.Require(
            "feature.step4",
            VersionGate.DefaultVersion,
            2,
            context => context.MinVersion);

        Assert.True(decision.IsSuccess);
        Assert.Equal(VersionGate.DefaultVersion, decision.Value.Version);
    }
}
