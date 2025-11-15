using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests;

public class VersionGateTests
{
    [Fact(Timeout = 15_000)]
    public void Require_ShouldRecordMaxVersion_WhenMissing()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var decision = gate.Require("feature.step1", VersionGate.DefaultVersion, 2);

        Assert.True(decision.IsSuccess);
        Assert.True(decision.Value.IsNew);
        Assert.Equal(2, decision.Value.Version);
    }

    [Fact(Timeout = 15_000)]
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

    [Fact(Timeout = 15_000)]
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

    [Fact(Timeout = 15_000)]
    public void Require_ShouldUseInitialVersionProvider()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var decision = gate.Require(
            "feature.step4",
            VersionGate.DefaultVersion,
            2,
            static context => context.MinVersion);

        Assert.True(decision.IsSuccess);
        Assert.Equal(VersionGate.DefaultVersion, decision.Value.Version);
    }

    [Fact(Timeout = 15_000)]
    public void Require_ShouldFail_WhenMinExceedsMax()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var decision = gate.Require("feature.invalid.range", 5, 4);

        Assert.True(decision.IsFailure);
        Assert.Equal(ErrorCodes.Validation, decision.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public void Require_ShouldFail_WhenRecordKindMismatch()
    {
        var store = new InMemoryDeterministicStateStore();
        var record = new DeterministicRecord("other.kind", 1, [], DateTimeOffset.UtcNow);
        store.Set("feature.kind.mismatch", record);
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var decision = gate.Require("feature.kind.mismatch", VersionGate.DefaultVersion, 3);

        Assert.True(decision.IsFailure);
        Assert.Equal(ErrorCodes.DeterministicReplay, decision.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public void Require_ShouldFail_WhenInitialProviderThrows()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var decision = gate.Require(
            "feature.initial.exception",
            VersionGate.DefaultVersion,
            3,
            static _ => throw new InvalidOperationException("boom"));

        Assert.True(decision.IsFailure);
        Assert.Equal(ErrorCodes.Exception, decision.Error?.Code);
        Assert.True(decision.Error!.Metadata.ContainsKey("changeId"));
        Assert.Equal("feature.initial.exception", decision.Error.Metadata["changeId"]);
    }

    [Fact(Timeout = 15_000)]
    public void Require_ShouldFail_WhenInitialVersionOutsideRange()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var decision = gate.Require(
            "feature.initial.outside",
            1,
            3,
            static _ => 5);

        Assert.True(decision.IsFailure);
        Assert.Equal(ErrorCodes.VersionConflict, decision.Error?.Code);
    }
}
