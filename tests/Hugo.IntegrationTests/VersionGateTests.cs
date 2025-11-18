using Shouldly;

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

        decision.IsSuccess.ShouldBeTrue();
        decision.Value.IsNew.ShouldBeTrue();
        decision.Value.Version.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public void Require_ShouldReturnPersistedVersion_OnSubsequentInvocations()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var first = gate.Require("feature.step2", VersionGate.DefaultVersion, 3);
        var second = gate.Require("feature.step2", VersionGate.DefaultVersion, 3);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        first.Value.IsNew.ShouldBeTrue();
        second.Value.IsNew.ShouldBeFalse();
        second.Value.Version.ShouldBe(first.Value.Version);
    }

    [Fact(Timeout = 15_000)]
    public void Require_ShouldFail_WhenPersistedVersionOutsideRange()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var initial = gate.Require("feature.step3", VersionGate.DefaultVersion, 5);
        initial.IsSuccess.ShouldBeTrue();

        var replay = gate.Require("feature.step3", 6, 7);

        replay.IsFailure.ShouldBeTrue();
        replay.Error?.Code.ShouldBe(ErrorCodes.VersionConflict);
    }

    [Fact(Timeout = 15_000)]
    public void Require_ShouldExposeConflictMetadata_WhenPersistedVersionExceedsMaximum()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var recorded = gate.Require("feature.step3.meta", VersionGate.DefaultVersion, 5);
        recorded.IsSuccess.ShouldBeTrue();

        var conflict = gate.Require("feature.step3.meta", 1, 4);

        conflict.IsFailure.ShouldBeTrue();
        conflict.Error?.Code.ShouldBe(ErrorCodes.VersionConflict);
        conflict.Error?.Metadata["persistedVersion"].ShouldBe(recorded.Value.Version);
        conflict.Error?.Metadata["minVersion"].ShouldBe(1);
        conflict.Error?.Metadata["maxVersion"].ShouldBe(4);
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

        decision.IsSuccess.ShouldBeTrue();
        decision.Value.Version.ShouldBe(VersionGate.DefaultVersion);
    }

    [Fact(Timeout = 15_000)]
    public void Require_ShouldFail_WhenMinExceedsMax()
    {
        var store = new InMemoryDeterministicStateStore();
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var decision = gate.Require("feature.invalid.range", 5, 4);

        decision.IsFailure.ShouldBeTrue();
        decision.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public void Require_ShouldFail_WhenRecordKindMismatch()
    {
        var store = new InMemoryDeterministicStateStore();
        var record = new DeterministicRecord("other.kind", 1, [], DateTimeOffset.UtcNow);
        store.Set("feature.kind.mismatch", record);
        var gate = new VersionGate(store, timeProvider: new FakeTimeProvider());

        var decision = gate.Require("feature.kind.mismatch", VersionGate.DefaultVersion, 3);

        decision.IsFailure.ShouldBeTrue();
        decision.Error?.Code.ShouldBe(ErrorCodes.DeterministicReplay);
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

        decision.IsFailure.ShouldBeTrue();
        decision.Error?.Code.ShouldBe(ErrorCodes.Exception);
        decision.Error!.Metadata.ContainsKey("changeId").ShouldBeTrue();
        decision.Error.Metadata["changeId"].ShouldBe("feature.initial.exception");
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

        decision.IsFailure.ShouldBeTrue();
        decision.Error?.Code.ShouldBe(ErrorCodes.VersionConflict);
    }
}
