using System.Threading.Channels;
using Hugo;
using Hugo.Primitives;
using static Hugo.Go;

namespace Hugo.Tests;

public class GoEdgeCaseTests
{
    [Fact]
    public async Task WaitGroup_AddTask_ShouldComplete_WhenTaskCancels()
    {
        var wg = new WaitGroup();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        var task = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), cts.Token);
        }, cts.Token);

        wg.Add(task);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);

        await wg.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, wg.Count);
    }

    [Fact]
    public async Task Mutex_Contention_ShouldSerializeCriticalSection()
    {
        var mutex = new HMutex();
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

        Assert.Equal(1, observedMax);
    }

    [Fact]
    public async Task Channel_WriteAsync_ShouldFail_WhenCompleted()
    {
        var channel = MakeChannel<int>(capacity: 1);
        channel.Writer.TryComplete();

        await Assert.ThrowsAsync<ChannelClosedException>(async () => await channel.Writer.WriteAsync(42, TestContext.Current.CancellationToken));
    }
}
