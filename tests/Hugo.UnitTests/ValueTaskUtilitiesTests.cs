using Shouldly;

namespace Hugo.Tests;

public sealed class ValueTaskUtilitiesTests
{
    [Fact(Timeout = 5_000)]
    public async Task ContinueWith_ShouldExposeCanceledTask()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var source = new ValueTask<int>(Task.FromCanceled<int>(cts.Token));
        bool invoked = false;

        await source.ContinueWith(task =>
        {
            invoked = true;
            var completed = task.AsTask();
            completed.IsCanceled.ShouldBeTrue();
            completed.Status.ShouldBe(TaskStatus.Canceled);
        });

        invoked.ShouldBeTrue();
    }

    [Fact(Timeout = 5_000)]
    public async Task ContinueWith_ShouldWrapThrownException()
    {
        var source = new ValueTask<int>(Task.FromException<int>(new InvalidOperationException("boom")));
        InvalidOperationException? captured = null;

        await source.ContinueWith(task =>
        {
            var completed = task.AsTask();
            completed.IsFaulted.ShouldBeTrue();
            captured = completed.Exception?.GetBaseException() as InvalidOperationException;
        });

        captured.ShouldNotBeNull();
        captured!.Message.ShouldBe("boom");
    }

    [Fact(Timeout = 5_000)]
    public async Task WhenAll_ShouldAwaitAllOperations()
    {
        var tcs1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tcs2 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var combined = ValueTaskUtilities.WhenAll(new[]
        {
            new ValueTask(tcs1.Task),
            new ValueTask(tcs2.Task)
        });

        tcs1.SetResult();
        tcs2.SetResult();

        await combined;
    }

    [Fact(Timeout = 5_000)]
    public async Task WhenAny_ShouldReturnIndexOfFirstCompletion()
    {
        var fast = new ValueTask(Task.CompletedTask);
        var slow = new ValueTask(Task.Delay(50, TestContext.Current.CancellationToken));

        var index = await ValueTaskUtilities.WhenAny(fast, slow);
        index.ShouldBe(0);

        var genericFast = new ValueTask<int>(1);
        var genericSlow = new ValueTask(Task.Delay(40, TestContext.Current.CancellationToken));

        var genericIndex = await ValueTaskUtilities.WhenAny(genericFast, genericSlow);
        genericIndex.ShouldBe(0);

        var slowGeneric = new ValueTask<int>(Task.Delay(40, TestContext.Current.CancellationToken).ContinueWith(_ => 1, TestContext.Current.CancellationToken));
        var invertedIndex = await ValueTaskUtilities.WhenAny(slowGeneric, new ValueTask(Task.CompletedTask));
        invertedIndex.ShouldBe(1);
    }

    [Fact(Timeout = 5_000)]
    public async Task WhenAll_ShouldReturnCompletedTaskForEmptyList()
    {
        var combined = ValueTaskUtilities.WhenAll(Array.Empty<ValueTask>());
        combined.IsCompleted.ShouldBeTrue();
        await combined;
    }

    [Fact(Timeout = 5_000)]
    public async Task ContinueWith_ShouldFlowSuccessfulResult()
    {
        var source = new ValueTask<int>(42);
        int observed = 0;

        await source.ContinueWith(task =>
        {
            task.AsTask().IsCompletedSuccessfully.ShouldBeTrue();
            observed = task.AsTask().Result;
        });

        observed.ShouldBe(42);
    }

    [Fact(Timeout = 5_000)]
    public async Task YieldAsync_ShouldYieldExecution()
    {
        var flag = false;
        var task = Task.Run(async () =>
        {
            flag = true;
            await ValueTaskUtilities.YieldAsync();
            flag.ShouldBeTrue();
        }, TestContext.Current.CancellationToken);

        await task;
    }

    [Fact(Timeout = 5_000)]
    public async Task WhenAny_ShouldReturnSecondIndexWhenSecondWins()
    {
        var slow = new ValueTask(Task.Delay(25, TestContext.Current.CancellationToken));
        var fast = new ValueTask(Task.CompletedTask);

        var index = await ValueTaskUtilities.WhenAny(slow, fast);
        index.ShouldBe(1);
    }
}
