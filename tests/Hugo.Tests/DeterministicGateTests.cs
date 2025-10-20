using System.Threading;
using Hugo;
using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests;

public class DeterministicGateTests
{
    [Fact]
    public async Task ExecuteAsync_ShouldPreferUpgradedPath_WhenVersionMatches()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var upgradedCount = 0;
        var legacyCount = 0;

        var first = await gate.ExecuteAsync<int>(
            "change.v1",
            1,
            2,
            ct =>
            {
                upgradedCount++;
                return Task.FromResult(Result.Ok(42));
            },
            ct =>
            {
                legacyCount++;
                return Task.FromResult(Result.Ok(21));
            },
            _ => 2,
            TestContext.Current.CancellationToken);

        var second = await gate.ExecuteAsync<int>(
            "change.v1",
            1,
            2,
            ct =>
            {
                upgradedCount++;
                return Task.FromResult(Result.Ok(11));
            },
            ct =>
            {
                legacyCount++;
                return Task.FromResult(Result.Ok(7));
            },
            null,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(42, first.Value);
        Assert.Equal(42, second.Value);
        Assert.Equal(1, upgradedCount);
        Assert.Equal(0, legacyCount);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldBridgeLegacyPath()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var upgradedCount = 0;
        var legacyCount = 0;

        var first = await gate.ExecuteAsync<int>(
            "change.v2",
            1,
            3,
            ct =>
            {
                upgradedCount++;
                return Task.FromResult(Result.Ok(100));
            },
            ct =>
            {
                legacyCount++;
                return Task.FromResult(Result.Ok(50));
            },
            _ => 1,
            TestContext.Current.CancellationToken);

        var second = await gate.ExecuteAsync<int>(
            "change.v2",
            1,
            3,
            ct =>
            {
                upgradedCount++;
                return Task.FromResult(Result.Ok(200));
            },
            ct =>
            {
                legacyCount++;
                return Task.FromResult(Result.Ok(75));
            },
            null,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(50, first.Value);
        Assert.Equal(50, second.Value);
        Assert.Equal(0, upgradedCount);
        Assert.Equal(1, legacyCount);
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSurfaceVersionGateFailures()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var result = await gate.ExecuteAsync<int>(
            "change.invalid",
            5,
            3,
            ct => Task.FromResult(Result.Ok(1)),
            ct => Task.FromResult(Result.Ok(0)),
            null,
            TestContext.Current.CancellationToken);

    Assert.True(result.IsFailure);
    Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact]
    public async Task Workflow_ShouldReplayDeterministicBranch()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var executions = 0;

        var workflow = gate.Workflow<int>("change.workflow", 1, 3, _ => 2)
            .ForVersion(1, (ctx, ct) => ctx.CaptureAsync("legacy", _ => Task.FromResult(Result.Ok(1)), ct))
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
                    ct).ConfigureAwait(false);
            })
            .WithFallback((ctx, ct) => ctx.CaptureAsync("fallback", _ => Task.FromResult(Result.Ok(5)), ct));

        var first = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);
        var second = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(99, first.Value);
        Assert.Equal(99, second.Value);
        Assert.Equal(1, executions);
        Assert.True(store.TryGet("change.workflow::v2::upgrade", out _));
    }

    [Fact]
    public async Task Workflow_ShouldRespectFallbackWhenNoBranchMatches()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var workflow = gate.Workflow<string>("change.workflow.fallback", 1, 3, _ => 3)
            .ForVersion(1, (ctx, ct) => Task.FromResult(Result.Ok("legacy")))
            .ForRange(1, 2, (ctx, ct) => Task.FromResult(Result.Ok("range")))
            .WithFallback((ctx, ct) => Task.FromResult(Result.Ok("fallback")));

        var result = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal("fallback", result.Value);
    }

    [Fact]
    public async Task Workflow_ShouldErrorWhenNoMatchingBranchOrFallback()
    {
        var store = new InMemoryDeterministicStateStore();
        var provider = new FakeTimeProvider();
        var versionGate = new VersionGate(store, provider);
        var effectStore = new DeterministicEffectStore(store, provider);
        var gate = new DeterministicGate(versionGate, effectStore);

        var workflow = gate.Workflow<int>("change.workflow.missing", 1, 2, _ => 2)
            .ForVersion(1, (ctx, ct) => Task.FromResult(Result.Ok(10)));

        var result = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);

    Assert.True(result.IsFailure);
    Assert.Equal(ErrorCodes.VersionConflict, result.Error?.Code);
    Assert.NotNull(result.Error);
    Assert.True(result.Error!.Metadata.TryGetValue("version", out var metadataVersion));
    Assert.Equal(2, Assert.IsType<int>(metadataVersion));
    }

    [Fact]
    public async Task Workflow_ShouldHonorPredicateOrdering()
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
                    return Task.FromResult(Result.Ok("new"));
                })
            .ForVersion(
                2,
                (ctx, ct) =>
                {
                    Interlocked.Increment(ref existingCount);
                    return Task.FromResult(Result.Ok("existing"));
                });

        var first = await workflow.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.Equal("new", first.Value);
        Assert.Equal(1, newCount);
        Assert.Equal(0, existingCount);

        var changeIdExisting = "change.workflow.predicates.existing";
        var decision = versionGate.Require(changeIdExisting, 1, 2, _ => 2);
        Assert.True(decision.IsSuccess);

        var newBranchHits = 0;
        var versionBranchHits = 0;

        var existingWorkflow = gate.Workflow<string>(changeIdExisting, 1, 2)
            .For(
                d => d.IsNew,
                (ctx, ct) =>
                {
                    Interlocked.Increment(ref newBranchHits);
                    return Task.FromResult(Result.Ok("new"));
                })
            .ForVersion(
                2,
                (ctx, ct) =>
                {
                    Interlocked.Increment(ref versionBranchHits);
                    return Task.FromResult(Result.Ok("existing"));
                });

        var second = await existingWorkflow.ExecuteAsync(TestContext.Current.CancellationToken);

        Assert.True(second.IsSuccess);
        Assert.Equal("existing", second.Value);
        Assert.Equal(0, newBranchHits);
        Assert.Equal(1, versionBranchHits);
    }
}
