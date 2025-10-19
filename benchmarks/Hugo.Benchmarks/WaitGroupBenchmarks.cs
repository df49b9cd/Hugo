using BenchmarkDotNet.Attributes;
using Hugo.Primitives;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
public class WaitGroupBenchmarks
{
    private const int IterationsPerTask = 16;

    [Params(16, 128)]
    public int TaskCount { get; set; }

    [Params(false, true)]
    public bool EnableCancellation { get; set; }

    [Benchmark]
    public async Task WaitGroupAsync()
    {
        using var cts = BenchmarkWorkloads.CreateCancellationScope(EnableCancellation);
        var wg = new WaitGroup();

        for (var i = 0; i < TaskCount; i++)
        {
            var taskIndex = i;
            wg.Go(async () => await ExecuteWorkerAsync(taskIndex, cts.Token).ConfigureAwait(false));
        }

        try
        {
            await wg.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (EnableCancellation)
        {
        }
    }

    [Benchmark]
    public async Task TaskWhenAllAsync()
    {
        using var cts = BenchmarkWorkloads.CreateCancellationScope(EnableCancellation);
        var tasks = new Task[TaskCount];

        for (var i = 0; i < TaskCount; i++)
        {
            var taskIndex = i;
            tasks[i] = Task.Run(() => ExecuteWorkerAsync(taskIndex, cts.Token), cts.Token);
        }

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (EnableCancellation)
        {
        }
    }

    [Benchmark]
    public async Task ManualContinuationAsync()
    {
        using var cts = BenchmarkWorkloads.CreateCancellationScope(EnableCancellation);
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var remaining = TaskCount;

        for (var i = 0; i < TaskCount; i++)
        {
            var taskIndex = i;
            _ = Task.Run(async () =>
            {
                try
                {
                    await ExecuteWorkerAsync(taskIndex, cts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (EnableCancellation)
                {
                }
                finally
                {
                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        if (cts.IsCancellationRequested)
                        {
                            tcs.TrySetCanceled(cts.Token);
                        }
                        else
                        {
                            tcs.TrySetResult();
                        }
                    }
                }
            }, CancellationToken.None);
        }

        try
        {
            await tcs.Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (EnableCancellation)
        {
        }
    }

    private static async Task ExecuteWorkerAsync(int taskIndex, CancellationToken token)
    {
        for (var i = 0; i < IterationsPerTask; i++)
        {
            token.ThrowIfCancellationRequested();
            BenchmarkWorkloads.SimulateCpuWork();
            await Task.Yield();
        }

        GC.KeepAlive(taskIndex);
    }
}
