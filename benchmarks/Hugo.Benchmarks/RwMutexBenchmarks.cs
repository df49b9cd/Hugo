using BenchmarkDotNet.Attributes;

using Microsoft.VisualStudio.Threading;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
internal class RwMutexBenchmarks
{
    [Params(32, 128)]
    public int OperationCount { get; set; }

    [Params(0.75d, 0.5d)]
    public double ReaderShare { get; set; }

    private int[]? _operationPlan;

    [IterationSetup]
    public void BuildPlan()
    {
        var plan = new int[OperationCount];
        var readerThreshold = (int)(ReaderShare * 100);
        var rng = new Random(OperationCount * 397 ^ readerThreshold);

        for (var i = 0; i < plan.Length; i++)
        {
            plan[i] = rng.Next(100) < readerThreshold ? 0 : 1;
        }

        _operationPlan = plan;
    }

    [Benchmark]
    public Task HugoRwMutexAsync() => ExecuteAsync(new HugoRwMutexStrategy());

    [Benchmark]
    public Task ReaderWriterLockSlimAsync() => ExecuteAsync(new ReaderWriterLockSlimStrategy());

    [Benchmark]
    public Task SemaphoreSlimHybridAsync() => ExecuteAsync(new SemaphoreSlimHybridStrategy());

    [Benchmark]
    public Task AsyncReaderWriterLockAsync() => ExecuteAsync(new AsyncReaderWriterLockStrategy());

    private Task ExecuteAsync(ILockStrategy strategy) => _operationPlan is null
            ? throw new InvalidOperationException("Operation plan was not initialized.")
            : strategy.ExecuteAsync(_operationPlan, CancellationToken.None);

    private interface ILockStrategy
    {
        Task ExecuteAsync(int[] operations, CancellationToken cancellationToken);
    }

    private sealed class HugoRwMutexStrategy : ILockStrategy
    {
        public async Task ExecuteAsync(int[] operations, CancellationToken cancellationToken)
        {
            var mutex = new RwMutex();
            var tasks = new Task[operations.Length];

            for (var i = 0; i < operations.Length; i++)
            {
                var isReader = operations[i] == 0;
                tasks[i] = Task.Run(async () =>
                {
                    if (isReader)
                    {
                        await using (await mutex.RLockAsync(cancellationToken).ConfigureAwait(false))
                        {
                            BenchmarkWorkloads.SimulateLightCpuWork();
                            await Task.Yield();
                        }
                    }
                    else
                    {
                        await using (await mutex.LockAsync(cancellationToken).ConfigureAwait(false))
                        {
                            BenchmarkWorkloads.SimulateCpuWork();
                        }
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }

    private sealed class ReaderWriterLockSlimStrategy : ILockStrategy
    {
        public Task ExecuteAsync(int[] operations, CancellationToken cancellationToken)
        {
            var rwLock = new ReaderWriterLockSlim(LockRecursionPolicy.NoRecursion);
            var tasks = new Task[operations.Length];

            for (var i = 0; i < operations.Length; i++)
            {
                var isReader = operations[i] == 0;
                tasks[i] = Task.Run(() =>
                {
                    if (isReader)
                    {
                        rwLock.EnterReadLock();
                        try
                        {
                            BenchmarkWorkloads.SimulateLightCpuWork();
                        }
                        finally
                        {
                            rwLock.ExitReadLock();
                        }
                    }
                    else
                    {
                        rwLock.EnterWriteLock();
                        try
                        {
                            BenchmarkWorkloads.SimulateCpuWork();
                        }
                        finally
                        {
                            rwLock.ExitWriteLock();
                        }
                    }
                }, cancellationToken);
            }

            return Task.WhenAll(tasks);
        }
    }

    private sealed class SemaphoreSlimHybridStrategy : ILockStrategy
    {
        public Task ExecuteAsync(int[] operations, CancellationToken cancellationToken)
        {
            var writerGate = new SemaphoreSlim(1, 1);
            var readerGate = new object();
            var activeReaders = 0;
            var tasks = new Task[operations.Length];

            for (var i = 0; i < operations.Length; i++)
            {
                var isReader = operations[i] == 0;
                tasks[i] = Task.Run(async () =>
                {
                    if (isReader)
                    {
                        var shouldLockWriter = false;
                        lock (readerGate)
                        {
                            activeReaders++;
                            if (activeReaders == 1)
                            {
                                shouldLockWriter = true;
                            }
                        }

                        if (shouldLockWriter)
                        {
                            await writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        }

                        try
                        {
                            BenchmarkWorkloads.SimulateLightCpuWork();
                            await Task.Yield();
                        }
                        finally
                        {
                            var releaseWriter = false;
                            lock (readerGate)
                            {
                                activeReaders--;
                                if (shouldLockWriter && activeReaders == 0)
                                {
                                    releaseWriter = true;
                                }
                            }

                            if (releaseWriter)
                            {
                                writerGate.Release();
                            }
                        }
                    }
                    else
                    {
                        await writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                        try
                        {
                            BenchmarkWorkloads.SimulateCpuWork();
                        }
                        finally
                        {
                            writerGate.Release();
                        }
                    }
                }, cancellationToken);
            }

            return Task.WhenAll(tasks);
        }
    }

    private sealed class AsyncReaderWriterLockStrategy : ILockStrategy
    {
        public async Task ExecuteAsync(int[] operations, CancellationToken cancellationToken)
        {
            using var context = new JoinableTaskContext();
            var asyncLock = new AsyncReaderWriterLock(context);
            var tasks = new Task[operations.Length];

            for (var i = 0; i < operations.Length; i++)
            {
                var isReader = operations[i] == 0;
                tasks[i] = Task.Run(async () =>
                {
                    if (isReader)
                    {
                        using (await asyncLock.ReadLockAsync(cancellationToken))
                        {
                            BenchmarkWorkloads.SimulateLightCpuWork();
                            await Task.Yield();
                        }
                    }
                    else
                    {
                        using (await asyncLock.WriteLockAsync(cancellationToken))
                        {
                            BenchmarkWorkloads.SimulateCpuWork();
                        }
                    }
                }, cancellationToken);
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
    }
}
