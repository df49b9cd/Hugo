using System.Collections.Generic;

namespace Hugo.Tests;

public class GoWaitGroupExtensionsTests
{
    [Fact]
    public void Go_ShouldThrow_WhenWaitGroupNull() =>
        Assert.Throws<ArgumentNullException>(static () => GoWaitGroupExtensions.Go(null!, static () => Task.CompletedTask));

    [Fact]
    public void Go_ShouldThrow_WhenFuncNull() =>
        Assert.Throws<ArgumentNullException>(static () => GoWaitGroupExtensions.Go(new WaitGroup(), (Func<Task>)null!));

    [Fact]
    public async Task Go_ShouldRunFunctionAndTrackWaitGroup()
    {
        var wg = new WaitGroup();
        var counter = 0;

        GoWaitGroupExtensions.Go(wg, async () =>
        {
            await Task.Yield();
            Interlocked.Increment(ref counter);
        });

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, counter);
        Assert.Equal(0, wg.Count);
    }

    [Fact]
    public void Go_WithCancellationToken_ShouldThrow_WhenWaitGroupNull() =>
        Assert.Throws<ArgumentNullException>(static () => GoWaitGroupExtensions.Go(null!, static _ => Task.CompletedTask, CancellationToken.None));

    [Fact]
    public void Go_WithCancellationToken_ShouldThrow_WhenFuncNull() =>
        Assert.Throws<ArgumentNullException>(static () => GoWaitGroupExtensions.Go(new WaitGroup(), (Func<CancellationToken, Task>)null!, CancellationToken.None));

    [Fact]
    public async Task Go_WithCancellationToken_ShouldPassTokenAndComplete()
    {
        var wg = new WaitGroup();
        using var cts = new CancellationTokenSource();
        var observed = new List<CancellationToken>();

        GoWaitGroupExtensions.Go(wg, async token =>
        {
            observed.Add(token);
            await Task.Yield();
        }, cts.Token);

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        var token = Assert.Single(observed);
        Assert.Equal(cts.Token, token);
        Assert.Equal(0, wg.Count);
    }

    [Fact]
    public async Task Go_WithCancellationToken_ShouldHandlePreCanceledToken()
    {
        var wg = new WaitGroup();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var invoked = false;

        GoWaitGroupExtensions.Go(wg, token =>
        {
            invoked = true;
            return Task.CompletedTask;
        }, cts.Token);

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(invoked);
        Assert.Equal(0, wg.Count);
    }

    [Fact]
    public async Task Go_WithScheduler_ShouldRunOnCustomScheduler()
    {
        var wg = new WaitGroup();
        var scheduler = new InlineTaskScheduler();
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        GoWaitGroupExtensions.Go(wg, async () =>
        {
            Assert.Equal(scheduler, TaskScheduler.Current);
            executed.TrySetResult();
            await Task.CompletedTask;
        }, scheduler: scheduler);

        await wg.WaitAsync(TestContext.Current.CancellationToken);
        await executed.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, scheduler.ExecutionCount);
    }

    private sealed class InlineTaskScheduler : TaskScheduler
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref _executionCount);
            TryExecuteTask(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            Interlocked.Increment(ref _executionCount);
            return TryExecuteTask(task);
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => null;
    }
}
