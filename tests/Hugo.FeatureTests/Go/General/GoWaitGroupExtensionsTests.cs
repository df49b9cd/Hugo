
namespace Hugo.Tests;

public class GoWaitGroupExtensionsTests
{
    [Fact(Timeout = 15_000)]
    public void Go_ShouldThrow_WhenWaitGroupNull() =>
        Should.Throw<ArgumentNullException>(static () => GoWaitGroupExtensions.Go(null!, static () => ValueTask.CompletedTask));

    [Fact(Timeout = 15_000)]
    public void Go_ShouldThrow_WhenFuncNull() =>
        Should.Throw<ArgumentNullException>(static () => GoWaitGroupExtensions.Go(new WaitGroup(), (Func<ValueTask>)null!));

    [Fact(Timeout = 15_000)]
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

        counter.ShouldBe(1);
        wg.Count.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public void Go_WithCancellationToken_ShouldThrow_WhenWaitGroupNull() =>
        Should.Throw<ArgumentNullException>(static () => GoWaitGroupExtensions.Go(null!, static _ => ValueTask.CompletedTask, cancellationToken: CancellationToken.None));

    [Fact(Timeout = 15_000)]
    public void Go_WithCancellationToken_ShouldThrow_WhenFuncNull() =>
        Should.Throw<ArgumentNullException>(static () => new WaitGroup().Go((Func<CancellationToken, ValueTask>)null!, cancellationToken: CancellationToken.None));

    [Fact(Timeout = 15_000)]
    public async Task Go_WithCancellationToken_ShouldPassTokenAndComplete()
    {
        var wg = new WaitGroup();
        using var cts = new CancellationTokenSource();
        var observed = new List<CancellationToken>();

        wg.Go(async token =>
        {
            observed.Add(token);
            await Task.Yield();
        }, cancellationToken: cts.Token);

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        var token = observed.ShouldHaveSingleItem();
        token.ShouldBe(cts.Token);
        wg.Count.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async Task Go_WithCancellationToken_ShouldHandlePreCanceledToken()
    {
        var wg = new WaitGroup();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var invoked = false;

        wg.Go(token =>
        {
            invoked = true;
            return ValueTask.CompletedTask;
        }, cancellationToken: cts.Token);

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        invoked.ShouldBeFalse();
        wg.Count.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async Task Go_WithScheduler_ShouldRunOnCustomScheduler()
    {
        var wg = new WaitGroup();
        var scheduler = new InlineTaskScheduler();
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        GoWaitGroupExtensions.Go(wg, async () =>
        {
            TaskScheduler.Current.ShouldBe(scheduler);
            executed.TrySetResult();
            await Task.CompletedTask;
        }, scheduler: scheduler);

        await wg.WaitAsync(TestContext.Current.CancellationToken);
        await executed.Task.WaitAsync(TestContext.Current.CancellationToken);

        scheduler.ExecutionCount.ShouldBe(1);
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
