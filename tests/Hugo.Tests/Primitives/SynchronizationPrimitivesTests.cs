using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

using Xunit;

namespace Hugo.Tests.Primitives;

public class SynchronizationPrimitivesTests
{
    [Fact]
    public async Task Mutex_EnterScope_ShouldProvideExclusiveAccess()
    {
        var mutex = new Mutex();
        var secondEntered = 0;
        using ManualResetEventSlim secondStarted = new(false);
        var sw = new System.Diagnostics.Stopwatch();

        var scope = mutex.EnterScope();
        Task second;
        try
        {
            second = Task.Run(() =>
            {
                secondStarted.Set();
                using (mutex.EnterScope())
                {
                    Interlocked.Exchange(ref secondEntered, 1);
                }
            }, TestContext.Current.CancellationToken);

            secondStarted.Wait(TestContext.Current.CancellationToken);
            sw.Start();
            var spinner = new SpinWait();
            while (sw.Elapsed < TimeSpan.FromMilliseconds(50))
            {
                if (Volatile.Read(ref secondEntered) != 0)
                {
                    break;
                }

                spinner.SpinOnce();
            }
            Assert.Equal(0, Volatile.Read(ref secondEntered));
        }
        finally
        {
            scope.Dispose();
        }

        await second.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, secondEntered);
    }

    [Fact]
    public async Task RwMutex_EnterReadScope_ShouldAllowConcurrentReadersAndBlockWriter()
    {
        var rwMutex = new RwMutex();
        using ManualResetEventSlim startReaders = new(false);
        object gate = new();
        int inside = 0;
        int maxInside = 0;

        void ReaderAction()
        {
            startReaders.Wait(TestContext.Current.CancellationToken);
            using (rwMutex.EnterReadScope())
            {
                int current = Interlocked.Increment(ref inside);
                lock (gate)
                {
                    maxInside = Math.Max(maxInside, current);
                }

                Thread.Sleep(50);
                Interlocked.Decrement(ref inside);
            }
        }

        Task reader1 = Task.Run(ReaderAction, TestContext.Current.CancellationToken);
        Task reader2 = Task.Run(ReaderAction, TestContext.Current.CancellationToken);
        startReaders.Set();
        await Task.WhenAll(reader1, reader2);
        Assert.Equal(2, maxInside);

        var readScope = rwMutex.EnterReadScope();
        using ManualResetEventSlim writerStarted = new(false);
        var writerEntered = 0;
        Task writer = Task.Run(() =>
        {
            writerStarted.Set();
            using (rwMutex.EnterWriteScope())
            {
                Interlocked.Exchange(ref writerEntered, 1);
            }
            Thread.Sleep(10);
        }, TestContext.Current.CancellationToken);

        writerStarted.Wait(TestContext.Current.CancellationToken);
        Thread.Sleep(50);
        Assert.Equal(0, Volatile.Read(ref writerEntered));
        readScope.Dispose();
        await writer.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, writerEntered);
    }

    [Fact]
    public async Task RwMutex_EnterWriteScope_ShouldBlockReadersUntilReleased()
    {
        var rwMutex = new RwMutex();
        var writeScope = rwMutex.EnterWriteScope();
        using ManualResetEventSlim readerStarted = new(false);
        var readerEntered = 0;

        Task reader = Task.Run(() =>
        {
            readerStarted.Set();
            using (rwMutex.EnterReadScope())
            {
                Interlocked.Exchange(ref readerEntered, 1);
                Thread.Sleep(10);
            }
        }, TestContext.Current.CancellationToken);

        readerStarted.Wait(TestContext.Current.CancellationToken);
        Thread.Sleep(50);
        Assert.Equal(0, Volatile.Read(ref readerEntered));

        writeScope.Dispose();
        await reader.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, readerEntered);
    }

    [Fact]
    public void Once_Do_ShouldThrow_WhenActionNull()
    {
        var once = new Once();
        Assert.Throws<ArgumentNullException>(() => once.Do(null!));
    }
}
