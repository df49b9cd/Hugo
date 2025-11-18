using Shouldly;

namespace Hugo.Tests;

public sealed class ValueTaskUtilitiesTests
{
    [Fact(Timeout = 15_000)]
    public async Task WhenAll_ShouldCompleteImmediately_WhenEmpty()
    {
        ValueTask result = ValueTaskUtilities.WhenAll(Array.Empty<ValueTask>());

        result.IsCompletedSuccessfully.ShouldBeTrue();
        await result;
    }

    [Fact(Timeout = 15_000)]
    public async Task WhenAny_ShouldReturnZero_WhenFirstCompletesFirst()
    {
        var first = ValueTask.CompletedTask;
        var second = new ValueTask(Task.Delay(50, TestContext.Current.CancellationToken));

        int winner = await ValueTaskUtilities.WhenAny(first, second);

        winner.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async Task WhenAny_Generic_ShouldReturnOne_WhenSecondCompletesFirst()
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new ValueTask<int>(tcs.Task);
        var fast = new ValueTask(Task.CompletedTask);

        int winner = await ValueTaskUtilities.WhenAny(slow, fast);

        winner.ShouldBe(1);
        tcs.TrySetResult(42);
    }

    [Fact(Timeout = 15_000)]
    public async Task ContinueWith_ShouldInvokeContinuation_WithCaughtException()
    {
        var failing = new ValueTask<int>(Task.FromException<int>(new InvalidOperationException("boom")));
        bool continuationInvoked = false;

        await failing.ContinueWith(result =>
        {
            continuationInvoked = true;
            result.AsTask().IsFaulted.ShouldBeTrue();
        });

        continuationInvoked.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task ContinueWith_ShouldInvokeContinuation_OnSuccess()
    {
        var success = new ValueTask<int>(Task.FromResult(7));
        int observed = 0;

        await success.ContinueWith(task => observed = task.Result);

        observed.ShouldBe(7);
    }

    [Fact(Timeout = 15_000)]
    public async Task YieldAsync_ShouldYieldOnce()
    {
        await ValueTaskUtilities.YieldAsync();
    }

    [Fact(Timeout = 15_000)]
    public async Task ContinueWith_ShouldSurfaceCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var canceled = new ValueTask<int>(Task.FromCanceled<int>(cts.Token));
        bool continuationInvoked = false;

        await canceled.ContinueWith(task =>
        {
            continuationInvoked = true;
            task.AsTask().IsCanceled.ShouldBeTrue();
        });

        continuationInvoked.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task WhenAll_ShouldAwaitAllOperations()
    {
        var completed = 0;
        var cancellationToken = TestContext.Current.CancellationToken;

        ValueTask first = new(Task.Run(async () =>
        {
            await Task.Delay(10, cancellationToken);
            Interlocked.Increment(ref completed);
        }, cancellationToken));

        ValueTask second = new(Task.Run(async () =>
        {
            await Task.Delay(15, cancellationToken);
            Interlocked.Increment(ref completed);
        }, cancellationToken));

        await ValueTaskUtilities.WhenAll(new[] { first, second });

        completed.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async Task WhenAny_DoubleGeneric_ShouldReturnOne_WhenSecondCompletesFirst()
    {
        var slow = new ValueTask<int>(Task.Run(async () =>
        {
            await Task.Delay(25, TestContext.Current.CancellationToken);
            return 1;
        }));

        var fast = new ValueTask<string>(Task.FromResult("fast"));

        int winner = await ValueTaskUtilities.WhenAny(slow, fast);

        winner.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async Task ContinueWith_ShouldExposeFaultedTask_WhenOperationThrows()
    {
        var source = new ValueTask<int>(Task.FromException<int>(new InvalidOperationException("boom")));
        var forwarded = new TaskCompletionSource<ValueTask<int>>(TaskCreationOptions.RunContinuationsAsynchronously);

        await source.ContinueWith(task => forwarded.SetResult(task));

        var observed = await forwarded.Task;
        observed.IsCompleted.ShouldBeTrue();
        observed.IsFaulted.ShouldBeTrue();
        await Should.ThrowAsync<InvalidOperationException>(async () => await observed);
    }

    [Fact(Timeout = 15_000)]
    public async Task ContinueWith_ShouldExposeCanceledTask_WhenOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var source = new ValueTask<int>(Task.FromCanceled<int>(cts.Token));
        var forwarded = new TaskCompletionSource<ValueTask<int>>(TaskCreationOptions.RunContinuationsAsynchronously);

        await source.ContinueWith(task => forwarded.SetResult(task));

        var observed = await forwarded.Task;
        observed.IsCanceled.ShouldBeTrue();
        var exception = await Should.ThrowAsync<OperationCanceledException>(async () => await observed);
        exception.CancellationToken.ShouldBe(cts.Token);
    }

    [Fact(Timeout = 15_000)]
    public async Task YieldAsync_ShouldDelayContinuation_UntilYieldCompletes()
    {
        var yielded = false;

        var yield = ValueTaskUtilities.YieldAsync();
        yielded = yield.IsCompleted;

        await yield;

        yielded.ShouldBeFalse();
    }
}
