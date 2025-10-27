using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

public class DeterministicEffectStoreTests
{
    [Fact]
    public async Task CaptureAsync_ShouldRecordAndReplaySuccessfulResult()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());
        var executionCount = 0;

        var first = await effectStore.CaptureAsync("effect.success", async ct =>
        {
            executionCount++;
            await Task.Yield();
            return Result.Ok(42);
        }, TestContext.Current.CancellationToken);

        var second = await effectStore.CaptureAsync("effect.success", _ =>
        {
            executionCount++;
            return Task.FromResult(Result.Ok(99));
        }, TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(42, first.Value);
        Assert.Equal(42, second.Value);
        Assert.Equal(1, executionCount);
    }

    [Fact]
    public async Task CaptureAsync_ShouldRecordAndReplayFailure()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());

        var first = await effectStore.CaptureAsync(
            "effect.failure",
            _ => Task.FromResult(Result.Fail<int>(Error.From("boom", ErrorCodes.Validation))),
            TestContext.Current.CancellationToken);

        var executed = false;
        var second = await effectStore.CaptureAsync(
            "effect.failure",
            _ =>
            {
                executed = true;
                return Task.FromResult(Result.Ok(99));
            },
            TestContext.Current.CancellationToken);

        Assert.True(first.IsFailure);
        Assert.Equal(ErrorCodes.Validation, first.Error?.Code);
        Assert.True(second.IsFailure);
        Assert.Equal(ErrorCodes.Validation, second.Error?.Code);
        Assert.False(executed);
    }

    [Fact]
    public async Task CaptureAsync_ShouldFail_WhenTypeMismatchDetected()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());

        var initial = await effectStore.CaptureAsync(
            "effect.type",
            _ => Task.FromResult(Result.Ok(17)),
            TestContext.Current.CancellationToken);
        Assert.True(initial.IsSuccess);

        var mismatchExecuted = false;
        var mismatch = await effectStore.CaptureAsync(
            "effect.type",
            _ =>
            {
                mismatchExecuted = true;
                return Task.FromResult(Result.Ok("seventeen"));
            },
            TestContext.Current.CancellationToken);

        Assert.True(mismatch.IsFailure);
        Assert.Equal(ErrorCodes.DeterministicReplay, mismatch.Error?.Code);
        Assert.False(mismatchExecuted);
    }

    [Fact]
    public async Task CaptureAsync_ShouldCaptureThrownExceptions()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());

        var first = await effectStore.CaptureAsync<Unit>("effect.exception", async ct =>
        {
            await Task.Yield();
            throw new InvalidOperationException("broken");
        }, TestContext.Current.CancellationToken);

        var exceptionExecuted = false;
        var second = await effectStore.CaptureAsync(
            "effect.exception",
            _ =>
            {
                exceptionExecuted = true;
                return Task.FromResult(Result.Ok(Unit.Value));
            },
            TestContext.Current.CancellationToken);

        Assert.True(first.IsFailure);
        Assert.Equal(ErrorCodes.Exception, first.Error?.Code);
        Assert.True(second.IsFailure);
        Assert.Equal(ErrorCodes.Exception, second.Error?.Code);
        Assert.False(exceptionExecuted);
    }

    [Fact]
    public async Task CaptureAsync_ShouldFail_WhenRecordKindMismatch()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());
        var record = new DeterministicRecord("hugo.version", 0, Array.Empty<byte>(), DateTimeOffset.UtcNow);
        store.Set("effect.kind", record);

        var mismatch = await effectStore.CaptureAsync(
            "effect.kind",
            static () => Result.Ok(7));

        Assert.True(mismatch.IsFailure);
        Assert.Equal(ErrorCodes.DeterministicReplay, mismatch.Error?.Code);
    }

    [Fact]
    public async Task CaptureAsync_ShouldRethrowCancellationWithoutRecording()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => effectStore.CaptureAsync<int>(
            "effect.canceled",
            async ct =>
            {
                await Task.Yield();
                throw new OperationCanceledException(ct);
            },
            cts.Token));

        Assert.False(store.TryGet("effect.canceled", out _));
    }

    [Fact]
    public async Task CaptureAsync_SynchronousOverload_ShouldPersistAndReplayValue()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());
        var executions = 0;

        var first = await effectStore.CaptureAsync(
            "effect.sync",
            () =>
            {
                executions++;
                return Result.Ok("value");
            });

        var second = await effectStore.CaptureAsync(
            "effect.sync",
            () =>
            {
                executions++;
                return Result.Ok("other");
            });

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal("value", first.Value);
        Assert.Equal("value", second.Value);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task CaptureAsync_ShouldSanitizeCancellationTokenMetadata()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var initial = await effectStore.CaptureAsync(
            "effect.cancellation",
            () => Result.Fail<int>(Error.Canceled(token: cts.Token)));

        Assert.True(initial.IsFailure);
        Assert.True(store.TryGet("effect.cancellation", out _));

        var replay = await effectStore.CaptureAsync(
            "effect.cancellation",
            () => Result.Ok(1));

        Assert.True(replay.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, replay.Error?.Code);
        Assert.NotNull(replay.Error);
        Assert.True(replay.Error!.Metadata.TryGetValue("cancellationToken", out var metadata));
        var tokenMetadata = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(metadata);
        Assert.True(tokenMetadata.TryGetValue("isCancellationRequested", out var requested));
        Assert.Equal(true, requested);
        Assert.True(tokenMetadata.TryGetValue("canBeCanceled", out var canBeCanceled));
        Assert.Equal(cts.Token.CanBeCanceled, canBeCanceled);
    }
}
