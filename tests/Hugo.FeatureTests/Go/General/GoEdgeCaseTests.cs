using System.Threading.Channels;


using static Hugo.Go;

namespace Hugo.Tests;

public class GoEdgeCaseTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask WaitGroup_AddTask_ShouldComplete_WhenTaskCancels()
    {
        var wg = new WaitGroup();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var task = Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        }, cancellationToken: cts.Token);

        wg.Add(task);

        await Should.ThrowAsync<OperationCanceledException>(async () => await task);

        await wg.WaitAsync(TestContext.Current.CancellationToken);
        wg.Count.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Mutex_Contention_ShouldSerializeCriticalSection()
    {
        using var mutex = new Mutex();
        var concurrent = 0;
        var observedMax = 0;

        var tasks = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () =>
            {
                await using (await mutex.LockAsync(TestContext.Current.CancellationToken))
                {
                    var inflight = Interlocked.Increment(ref concurrent);
                    Interlocked.Exchange(ref observedMax, Math.Max(observedMax, inflight));
                    await Task.Delay(5, TestContext.Current.CancellationToken);
                    Interlocked.Decrement(ref concurrent);
                }
            }, TestContext.Current.CancellationToken)).ToArray();

        await Task.WhenAll(tasks);

        observedMax.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Channel_WriteAsync_ShouldFail_WhenCompleted()
    {
        var channel = MakeChannel<int>(capacity: 1);
        channel.Writer.TryComplete();

        await Should.ThrowAsync<ChannelClosedException>(async () => await channel.Writer.WriteAsync(42, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WaitAsync_ShouldThrow_WhenCanceledBeforeOperationsFinish()
    {
        var wg = new WaitGroup();
        var blocker = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        wg.Go(new ValueTask(blocker.Task));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(async () => await wg.WaitAsync(cts.Token));

        blocker.TrySetResult();
        await wg.WaitAsync(TestContext.Current.CancellationToken);
    }
}
