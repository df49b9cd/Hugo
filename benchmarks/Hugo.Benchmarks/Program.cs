using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

namespace Hugo.Benchmarks;

public static class Program
{
    public static void Main(string[] args) => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}

[MemoryDiagnoser]
public class MutexBenchmarks
{
    private const int IterationsPerTask = 32;

    [Params(16, 64)]
    public int TaskCount { get; set; }

    [Benchmark(Baseline = true)]
    public Task HugoMutexAsync() => RunAsync(new HugoMutexAsyncStrategy());

    [Benchmark]
    public Task SemaphoreSlimAsync() => RunAsync(new SemaphoreSlimAsyncStrategy());

    [Benchmark]
    public Task HugoMutexEnterScopeAsync() => RunSyncAsync(new HugoMutexSyncStrategy());

    [Benchmark]
    public Task MonitorLockScopeAsync() => RunSyncAsync(new MonitorLockStrategy());

    private async Task RunAsync(IAsyncLockStrategy strategy)
    {
        var shared = 0;
        var tasks = new Task[TaskCount];

        for (var i = 0; i < TaskCount; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                for (var iteration = 0; iteration < IterationsPerTask; iteration++)
                {
                    await using var releaser = await strategy.EnterAsync().ConfigureAwait(false);
                    BenchmarkWorkloads.SimulateLightCpuWork();
                    Interlocked.Increment(ref shared);
                }
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        strategy.Cleanup();
        GC.KeepAlive(shared);
    }

    private async Task RunSyncAsync(ISyncLockStrategy strategy)
    {
        var shared = 0;
        var tasks = new Task[TaskCount];

        for (var i = 0; i < TaskCount; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                for (var iteration = 0; iteration < IterationsPerTask; iteration++)
                {
                    strategy.Execute(() =>
                    {
                        BenchmarkWorkloads.SimulateLightCpuWork();
                        Interlocked.Increment(ref shared);
                    });
                }
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        strategy.Cleanup();
        GC.KeepAlive(shared);
    }

    private interface IAsyncLockStrategy
    {
        ValueTask<IAsyncDisposable> EnterAsync();
        void Cleanup();
    }

    private interface ISyncLockStrategy
    {
        void Execute(Action criticalSection);
        void Cleanup();
    }

    private sealed class HugoMutexAsyncStrategy : IAsyncLockStrategy
    {
        private readonly Mutex _mutex = new();

        public async ValueTask<IAsyncDisposable> EnterAsync()
        {
            var releaser = await _mutex.LockAsync().ConfigureAwait(false);
            return new AsyncReleaser(releaser);
        }

        public void Cleanup()
        {
        }

        private sealed class AsyncReleaser(Mutex.AsyncLockReleaser releaser) : IAsyncDisposable
        {
            private Mutex.AsyncLockReleaser _releaser = releaser;
            private bool _disposed;

            public ValueTask DisposeAsync()
            {
                if (_disposed)
                {
                    return ValueTask.CompletedTask;
                }

                _disposed = true;
                return _releaser.DisposeAsync();
            }
        }
    }

    private sealed class SemaphoreSlimAsyncStrategy : IAsyncLockStrategy
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public async ValueTask<IAsyncDisposable> EnterAsync()
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            return new Releaser(_semaphore);
        }

        public void Cleanup() => _semaphore.Dispose();

        private sealed class Releaser(SemaphoreSlim semaphore) : IAsyncDisposable
        {
            private SemaphoreSlim? _semaphore = semaphore;

            public ValueTask DisposeAsync()
            {
                _semaphore?.Release();
                _semaphore = null;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class HugoMutexSyncStrategy : ISyncLockStrategy
    {
        private readonly Mutex _mutex = new();

        public void Execute(Action criticalSection)
        {
            using var scope = _mutex.EnterScope();
            criticalSection();
        }

        public void Cleanup()
        {
        }
    }

    private sealed class MonitorLockStrategy : ISyncLockStrategy
    {
        private readonly Lock _gate = new();

        public void Execute(Action criticalSection)
        {
            lock (_gate)
            {
                criticalSection();
            }
        }

        public void Cleanup()
        {
        }
    }
}
