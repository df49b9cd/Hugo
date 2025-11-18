using Shouldly;

using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

public class DeterministicEffectStoreTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask CaptureAsync_ShouldRecordAndReplaySuccessfulResult()
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
            return ValueTask.FromResult(Result.Ok(99));
        }, TestContext.Current.CancellationToken);

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        first.Value.ShouldBe(42);
        second.Value.ShouldBe(42);
        executionCount.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CaptureAsync_ShouldRecordAndReplayFailure()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());

        var first = await effectStore.CaptureAsync(
            "effect.failure",
            _ => ValueTask.FromResult(Result.Fail<int>(Error.From("boom", ErrorCodes.Validation))),
            TestContext.Current.CancellationToken);

        var executed = false;
        var second = await effectStore.CaptureAsync(
            "effect.failure",
            _ =>
            {
                executed = true;
                return ValueTask.FromResult(Result.Ok(99));
            },
            TestContext.Current.CancellationToken);

        first.IsFailure.ShouldBeTrue();
        first.Error?.Code.ShouldBe(ErrorCodes.Validation);
        second.IsFailure.ShouldBeTrue();
        second.Error?.Code.ShouldBe(ErrorCodes.Validation);
        executed.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CaptureAsync_ShouldFail_WhenTypeMismatchDetected()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());

        var initial = await effectStore.CaptureAsync(
            "effect.type",
            _ => ValueTask.FromResult(Result.Ok(17)),
            TestContext.Current.CancellationToken);
        initial.IsSuccess.ShouldBeTrue();

        var mismatchExecuted = false;
        var mismatch = await effectStore.CaptureAsync(
            "effect.type",
            _ =>
            {
                mismatchExecuted = true;
                return ValueTask.FromResult(Result.Ok("seventeen"));
            },
            TestContext.Current.CancellationToken);

        mismatch.IsFailure.ShouldBeTrue();
        mismatch.Error?.Code.ShouldBe(ErrorCodes.DeterministicReplay);
        mismatchExecuted.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CaptureAsync_ShouldCaptureThrownExceptions()
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
                return ValueTask.FromResult(Result.Ok(Unit.Value));
            },
            TestContext.Current.CancellationToken);

        first.IsFailure.ShouldBeTrue();
        first.Error?.Code.ShouldBe(ErrorCodes.Exception);
        second.IsFailure.ShouldBeTrue();
        second.Error?.Code.ShouldBe(ErrorCodes.Exception);
        exceptionExecuted.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CaptureAsync_ShouldFail_WhenRecordKindMismatch()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());
        var record = new DeterministicRecord("hugo.version", 0, [], DateTimeOffset.UtcNow);
        store.Set("effect.kind", record);

        var mismatch = await effectStore.CaptureAsync<int>(
            "effect.kind",
            static () => Result.Ok(7));

        mismatch.IsFailure.ShouldBeTrue();
        mismatch.Error?.Code.ShouldBe(ErrorCodes.DeterministicReplay);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CaptureAsync_ShouldRethrowCancellationWithoutRecording()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());
        using var cts = new CancellationTokenSource();

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
        {
            await effectStore.CaptureAsync<int>(
                "effect.canceled",
                async ct =>
                {
                    await Task.Yield();
                    throw new OperationCanceledException(ct);
                },
                cts.Token);
        });

        store.TryGet("effect.canceled", out _).ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CaptureAsync_SynchronousOverload_ShouldPersistAndReplayValue()
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

        first.IsSuccess.ShouldBeTrue();
        second.IsSuccess.ShouldBeTrue();
        first.Value.ShouldBe("value");
        second.Value.ShouldBe("value");
        executions.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask CaptureAsync_ShouldSanitizeCancellationTokenMetadata()
    {
        var store = new InMemoryDeterministicStateStore();
        var effectStore = new DeterministicEffectStore(store, new FakeTimeProvider());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var initial = await effectStore.CaptureAsync(
            "effect.cancellation",
            () => Result.Fail<int>(Error.Canceled(token: cts.Token)));

        initial.IsFailure.ShouldBeTrue();
        store.TryGet("effect.cancellation", out _).ShouldBeTrue();

        var replay = await effectStore.CaptureAsync(
            "effect.cancellation",
            () => Result.Ok(1));

        replay.IsFailure.ShouldBeTrue();
        replay.Error?.Code.ShouldBe(ErrorCodes.Canceled);
        replay.Error.ShouldNotBeNull();
        replay.Error!.Metadata.TryGetValue("cancellationToken", out var metadata).ShouldBeTrue();
        var tokenMetadata = metadata.ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>();
        tokenMetadata!.TryGetValue("isCancellationRequested", out var requested).ShouldBeTrue();
        requested.ShouldBe(true);
        tokenMetadata.TryGetValue("canBeCanceled", out var canBeCanceled).ShouldBeTrue();
        canBeCanceled.ShouldBe(cts.Token.CanBeCanceled);
    }
}
