using System.Diagnostics;

namespace Hugo.Tests.Primitives;

internal class SynchronizationPrimitivesTests
{
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WriterDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(2);

    private static void AssertIncomplete(Task task, TimeSpan timeout, CancellationToken cancellationToken)
    {
        Assert.False(task.Wait(timeout, cancellationToken), "Task completed before expected.");
    }

    private static void AssertWait(ManualResetEventSlim gate, string message, CancellationToken cancellationToken)
    {
        Assert.True(gate.Wait(WaitTimeout, cancellationToken), message);
    }

    private static void BusyWait(TimeSpan duration, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        var spinner = new SpinWait();
        while (sw.Elapsed < duration)
        {
            cancellationToken.ThrowIfCancellationRequested();
            spinner.SpinOnce();
        }
    }

    [Fact]
    public async Task Mutex_EnterScope_ShouldProvideExclusiveAccess()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var mutex = new Mutex();
        var secondEntered = 0;
        using ManualResetEventSlim secondStarted = new(false);

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
            }, cancellation);

            AssertWait(secondStarted, "Timed out waiting for second mutex waiter to start.", cancellation);
            AssertIncomplete(second, ShortDelay, cancellation);
            Assert.Equal(0, Volatile.Read(ref secondEntered));
        }
        finally
        {
            scope.Dispose();
        }

        await second.WaitAsync(cancellation);
        Assert.Equal(1, secondEntered);
    }

    [Fact]
    public async Task RwMutex_EnterReadScope_ShouldAllowConcurrentReadersAndBlockWriter()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var rwMutex = new RwMutex();
        using ManualResetEventSlim startReaders = new(false);
        object gate = new();
        int inside = 0;
        int maxInside = 0;

        void ReaderAction()
        {
            Assert.True(startReaders.Wait(WaitTimeout, cancellation), "Timed out waiting to start readers.");
            using (rwMutex.EnterReadScope())
            {
                int current = Interlocked.Increment(ref inside);
                lock (gate)
                {
                    maxInside = Math.Max(maxInside, current);
                }

                BusyWait(ShortDelay, cancellation);
                Interlocked.Decrement(ref inside);
            }
        }

        Task StartReader() => Task.Factory.StartNew(
            ReaderAction,
            cancellation,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default);

        Task reader1 = StartReader();
        Task reader2 = StartReader();
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
            BusyWait(WriterDelay, cancellation);
        }, cancellation);

        AssertWait(writerStarted, "Timed out waiting for writer to start.", cancellation);
        AssertIncomplete(writer, ShortDelay, cancellation);
        Assert.Equal(0, Volatile.Read(ref writerEntered));
        readScope.Dispose();
        await writer.WaitAsync(cancellation);
        Assert.Equal(1, writerEntered);
    }

    [Fact]
    public async Task RwMutex_EnterWriteScope_ShouldBlockReadersUntilReleased()
    {
        var cancellation = TestContext.Current.CancellationToken;
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
                BusyWait(WriterDelay, cancellation);
            }
        }, cancellation);

        AssertWait(readerStarted, "Timed out waiting for reader to start.", cancellation);
        AssertIncomplete(reader, ShortDelay, cancellation);
        Assert.Equal(0, Volatile.Read(ref readerEntered));

        writeScope.Dispose();
        await reader.WaitAsync(cancellation);
        Assert.Equal(1, readerEntered);
    }

    [Fact]
    public void Once_Do_ShouldThrow_WhenActionNull()
    {
        var once = new Once();
        Assert.Throws<ArgumentNullException>(() => once.Do(null!));
    }
}
