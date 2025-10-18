using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Hugo;

BenchmarkRunner.Run<MutexBenchmarks>();

[MemoryDiagnoser]
public class MutexBenchmarks
{
    private const int TaskCount = 16;
    private const int IterationsPerTask = 32;

    [Benchmark]
    public Task HugoMutexAsync() => RunAsync(new MutexScope());

    [Benchmark(Baseline = true)]
    public Task SemaphoreSlimAsync() => RunAsync(new SemaphoreSlimScope());

    private static async Task RunAsync(ILockScope scope)
    {
        var shared = 0;
        var tasks = new Task[TaskCount];

        for (var i = 0; i < TaskCount; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                for (var j = 0; j < IterationsPerTask; j++)
                {
                    await using var releaser = await scope.EnterAsync().ConfigureAwait(false);
                    shared++;
                }
            });
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        scope.Cleanup();

        // ensure shared is consumed to avoid dead-code elimination
        GC.KeepAlive(shared);
    }

    private interface ILockScope
    {
        ValueTask<IAsyncDisposable> EnterAsync();
        void Cleanup();
    }

    private sealed class MutexScope : ILockScope
    {
        private readonly Hugo.Mutex _mutex = new();

        public async ValueTask<IAsyncDisposable> EnterAsync()
        {
            var releaser = await _mutex.LockAsync().ConfigureAwait(false);
            return new AsyncReleaser(releaser);
        }

        public void Cleanup()
        {
            // mutex has no resources to dispose
        }

        private sealed class AsyncReleaser : IAsyncDisposable
        {
            private Hugo.Mutex.AsyncLockReleaser _releaser;

            public AsyncReleaser(Hugo.Mutex.AsyncLockReleaser releaser) => _releaser = releaser;

            public ValueTask DisposeAsync() => _releaser.DisposeAsync();
        }
    }

    private sealed class SemaphoreSlimScope : ILockScope
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public async ValueTask<IAsyncDisposable> EnterAsync()
        {
            await _semaphore.WaitAsync().ConfigureAwait(false);
            return new Releaser(_semaphore);
        }

        public void Cleanup() => _semaphore.Dispose();

        private sealed class Releaser : IAsyncDisposable
        {
            private readonly SemaphoreSlim _semaphore;

            public Releaser(SemaphoreSlim semaphore) => _semaphore = semaphore;

            public ValueTask DisposeAsync()
            {
                _semaphore.Release();
                return ValueTask.CompletedTask;
            }
        }
    }
}
