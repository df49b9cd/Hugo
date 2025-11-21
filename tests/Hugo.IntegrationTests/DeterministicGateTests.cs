using System.Diagnostics.CodeAnalysis;

using Microsoft.Extensions.Time.Testing;


namespace Hugo.Tests;

public class DeterministicGateTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask ExecuteAsync_ShouldPreferUpgradedPath_WhenVersionMatches()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var upgradedCount = 0;
        var legacyCount = 0;

        var first = await gate.ExecuteAsync(
            "change.v1",
            1,
            2,
            ct =>
            {
                upgradedCount++;
                return ValueTask.FromResult(Result.Ok(42));
            },
            ct =>
            {
                legacyCount++;
                return ValueTask.FromResult(Result.Ok(21));
            },
            _ => 2,
            TestContext.Current.CancellationToken);

        var second = await gate.ExecuteAsync(
            "change.v1",
            1,
            2,
            ct =>
            {
                upgradedCount++;
                return ValueTask.FromResult(Result.Ok(11));
            },
            ct =>
            {
                legacyCount++;
                return ValueTask.FromResult(Result.Ok(7));
            },
            null,
            TestContext.Current.CancellationToken);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        first.Value.ShouldBe(42);
        second.Value.ShouldBe(42);
        upgradedCount.ShouldBe(1);
        legacyCount.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ExecuteAsync_ShouldBridgeLegacyPath()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var upgradedCount = 0;
        var legacyCount = 0;

        var first = await gate.ExecuteAsync(
            "change.v2",
            1,
            3,
            ct =>
            {
                upgradedCount++;
                return ValueTask.FromResult(Result.Ok(100));
            },
            ct =>
            {
                legacyCount++;
                return ValueTask.FromResult(Result.Ok(50));
            },
            _ => 1,
            TestContext.Current.CancellationToken);

        var second = await gate.ExecuteAsync(
            "change.v2",
            1,
            3,
            ct =>
            {
                upgradedCount++;
                return ValueTask.FromResult(Result.Ok(200));
            },
            ct =>
            {
                legacyCount++;
                return ValueTask.FromResult(Result.Ok(75));
            },
            null,
            TestContext.Current.CancellationToken);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        first.Value.ShouldBe(50);
        second.Value.ShouldBe(50);
        upgradedCount.ShouldBe(0);
        legacyCount.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ExecuteAsync_ShouldSurfaceVersionGateFailures()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var result = await gate.ExecuteAsync(
            "change.invalid",
            5,
            3,
            static ct => ValueTask.FromResult(Result.Ok(1)),
            static ct => ValueTask.FromResult(Result.Ok(0)),
            null,
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Workflow_ShouldReplayDeterministicBranch()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var executions = 0;

        var workflow = gate.Workflow<int>("change.workflow", 1, 3, _ => 2)
            .ForVersion(1, (ctx, ct) => ctx.CaptureAsync("legacy", _ => ValueTask.FromResult(Result.Ok(1)), ct))
            .ForVersion(2, async (ctx, ct) =>
            {
                return await ctx.CaptureAsync(
                    "upgrade",
                    async token =>
                    {
                        Interlocked.Increment(ref executions);
                        await Task.Delay(10, token);
                        return Result.Ok(99);
                    },
                    ct);
            })
            .WithFallback((ctx, ct) => ctx.CaptureAsync("fallback", _ => ValueTask.FromResult(Result.Ok(5)), ct));

        var first = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);
        var second = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        first.Value.ShouldBe(99);
        second.Value.ShouldBe(99);
        executions.ShouldBe(1);
        store.TryGet("change.workflow::v2::upgrade", out _).ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Workflow_ShouldRespectFallbackWhenNoBranchMatches()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var workflow = gate.Workflow<string>("change.workflow.fallback", 1, 3, static _ => 3)
            .ForVersion(1, static (ctx, ct) => ValueTask.FromResult(Result.Ok("legacy")))
            .ForRange(1, 2, static (ctx, ct) => ValueTask.FromResult(Result.Ok("range")))
            .WithFallback(static (ctx, ct) => ValueTask.FromResult(Result.Ok("fallback")));

        var result = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe("fallback");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Workflow_ShouldErrorWhenNoMatchingBranchOrFallback()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var workflow = gate.Workflow<int>("change.workflow.missing", 1, 2, static _ => 2)
            .ForVersion(1, static (ctx, ct) => ValueTask.FromResult(Result.Ok(10)));

        var result = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.VersionConflict);
        result.Error.ShouldNotBeNull();
        result.Error!.Metadata.TryGetValue("version", out var metadataVersion).ShouldBeTrue();
        metadataVersion.ShouldBeOfType<int>().ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Workflow_ShouldHonorPredicateOrdering()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var newCount = 0;
        var existingCount = 0;

        var workflow = gate.Workflow<string>("change.workflow.predicates.new", 1, 2, _ => 2)
            .For(
                decision => decision.IsNew,
                (ctx, ct) =>
                {
                    Interlocked.Increment(ref newCount);
                    return ValueTask.FromResult(Result.Ok("new"));
                })
            .ForVersion(
                2,
                (ctx, ct) =>
                {
                    Interlocked.Increment(ref existingCount);
                    return ValueTask.FromResult(Result.Ok("existing"));
                });

        var first = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);

        first.IsSuccess.ShouldBeTrue();
        first.Value.ShouldBe("new");
        newCount.ShouldBe(1);
        existingCount.ShouldBe(0);

        var changeIdExisting = "change.workflow.predicates.existing";
        var decision = versionGate.Require(changeIdExisting, 1, 2, _ => 2);
        decision.IsSuccess.ShouldBeTrue();

        var newBranchHits = 0;
        var versionBranchHits = 0;

        var existingWorkflow = gate.Workflow<string>(changeIdExisting, 1, 2)
            .For(
                d => d.IsNew,
                (ctx, ct) =>
                {
                    Interlocked.Increment(ref newBranchHits);
                    return ValueTask.FromResult(Result.Ok("new"));
                })
            .ForVersion(
                2,
                (ctx, ct) =>
                {
                    Interlocked.Increment(ref versionBranchHits);
                    return ValueTask.FromResult(Result.Ok("existing"));
                });

        var second = await existingWorkflow.ExecuteAsync(TestContext.Current.CancellationToken);

        second.IsSuccess.ShouldBeTrue();
        second.Value.ShouldBe("existing");
        newBranchHits.ShouldBe(0);
        versionBranchHits.ShouldBe(1);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public void Workflow_ForVersion_ShouldThrow_WhenVersionOutsideBounds(int version)
    {
        var (gate, _, _) = CreateGate();
        var builder = gate.Workflow<int>("change.workflow.bounds.version", 1, 2);

        Should.Throw<ArgumentOutOfRangeException>(() =>
            builder.ForVersion(version, (ctx, ct) => ValueTask.FromResult(Result.Ok(1))));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 4)]
    [InlineData(3, 2)]
    public void Workflow_ForRange_ShouldThrow_WhenRangeOutsideBounds(int minVersion, int maxVersion)
    {
        var (gate, _, _) = CreateGate();
        var builder = gate.Workflow<int>("change.workflow.bounds.range", 1, 3);

        Should.Throw<ArgumentOutOfRangeException>(() =>
            builder.ForRange(minVersion, maxVersion, (ctx, ct) => ValueTask.FromResult(Result.Ok(1))));
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WorkflowContext_CreateEffectId_ShouldNormalizeScope()
    {
        var (gate, store, _) = CreateGate();
        string? effectId = null;

        var workflow = gate.Workflow<int>("change.workflow.scope", 1, 1, _ => 1)
            .ForVersion(1, (ctx, ct) =>
            {
                effectId = ctx.CreateEffectId("  step  ");
                return ctx.CaptureAsync("  step  ", () => Result.Ok(5), ct);
            });

        var result = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        effectId.ShouldBe("change.workflow.scope::v1::step");
        store.TryGet("change.workflow.scope::v1::step", out _).ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Workflow_ShouldFail_WhenStepIdentifierMissing()
    {
        var (gate, _, _) = CreateGate();

        var workflow = gate.Workflow<int>("change.workflow.invalidstep", 1, 1, static _ => 1)
            .ForVersion(1, static (ctx, ct) => ctx.CaptureAsync(string.Empty, static () => Result.Ok(1), ct));

        var result = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
    }

    [RequiresDynamicCode("Calls Hugo.VersionGate.VersionGate(IDeterministicStateStore, TimeProvider, JsonSerializerOptions)")]
    private static (DeterministicGate Gate, InMemoryDeterministicStateStore Store, FakeTimeProvider TimeProvider) CreateGate()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        return (gate, store, provider);
    }
}
