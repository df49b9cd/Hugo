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
}
