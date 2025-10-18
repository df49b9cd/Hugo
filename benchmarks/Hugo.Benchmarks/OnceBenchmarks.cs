using BenchmarkDotNet.Attributes;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
public class OnceBenchmarks
{
    [Params(16, 64)]
    public int TaskCount { get; set; }

    private int _initializationCount;

    [IterationSetup]
    public void Reset() => Volatile.Write(ref _initializationCount, 0);

    [Benchmark]
    public async Task HugoOnceAsync()
    {
        var once = new Hugo.Once();
        var tasks = new Task[TaskCount];

        for (var i = 0; i < TaskCount; i++)
        {
            tasks[i] = Task.Run(() => once.Do(() => Initialize()));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        GC.KeepAlive(_initializationCount);
    }

    [Benchmark]
    public async Task LazyValueAsync()
    {
        var lazy = new Lazy<int>(() => Initialize(), LazyThreadSafetyMode.ExecutionAndPublication);
        var tasks = new Task[TaskCount];

        for (var i = 0; i < TaskCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                _ = lazy.Value;
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        GC.KeepAlive(lazy.Value);
    }

    [Benchmark]
    public async Task LazyInitializerAsync()
    {
        int target = 0;
        bool initialized = false;
        object sync = new object();
        var tasks = new Task[TaskCount];

        for (var i = 0; i < TaskCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                LazyInitializer.EnsureInitialized(ref target, ref initialized, ref sync, Initialize);
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        GC.KeepAlive(target);
    }

    [Benchmark]
    public async Task DoubleCheckedLockAsync()
    {
        var value = 0;
        var gate = new object();
        var tasks = new Task[TaskCount];

        for (var i = 0; i < TaskCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                if (Volatile.Read(ref value) == 0)
                {
                    lock (gate)
                    {
                        if (value == 0)
                        {
                            value = Initialize();
                        }
                    }
                }
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        GC.KeepAlive(value);
    }

    private int Initialize()
    {
        BenchmarkWorkloads.SimulateCpuWork();
        return Interlocked.Increment(ref _initializationCount);
    }
}
