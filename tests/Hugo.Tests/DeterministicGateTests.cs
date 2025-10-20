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
}
