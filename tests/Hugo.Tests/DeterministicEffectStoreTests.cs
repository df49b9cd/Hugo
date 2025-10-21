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

        var first = await effectStore.CaptureAsync<int>("effect.success", async ct =>
        {
            executionCount++;
            await Task.Yield();
            return Result.Ok(42);
        }, TestContext.Current.CancellationToken);

        var second = await effectStore.CaptureAsync<int>("effect.success", _ =>
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

        var first = await effectStore.CaptureAsync<int>(
            "effect.failure",
            _ => Task.FromResult(Result.Fail<int>(Error.From("boom", ErrorCodes.Validation))),
            TestContext.Current.CancellationToken);

        var executed = false;
        var second = await effectStore.CaptureAsync<int>(
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

        var initial = await effectStore.CaptureAsync<int>(
            "effect.type",
            _ => Task.FromResult(Result.Ok(17)),
            TestContext.Current.CancellationToken);
        Assert.True(initial.IsSuccess);

        var mismatchExecuted = false;
        var mismatch = await effectStore.CaptureAsync<string>(
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
        var second = await effectStore.CaptureAsync<Unit>(
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
}
