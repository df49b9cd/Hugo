using System.Diagnostics;


namespace Hugo.Tests.Primitives;

public class SynchronizationPrimitivesTests
{
    private static readonly TimeSpan ShortDelay = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan WriterDelay = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    private static void AssertIncomplete(ValueTask task, TimeSpan timeout, CancellationToken cancellationToken) => task.AsTask().Wait(timeout, cancellationToken).ShouldBeFalse("Task completed before expected.");

    private static void AssertWait(ManualResetEventSlim gate, string message, CancellationToken cancellationToken) => gate.Wait(WaitTimeout, cancellationToken).ShouldBeTrue(message);

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

    [Fact(Timeout = 15_000)]
    public async ValueTask Mutex_EnterScope_ShouldProvideExclusiveAccess()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var mutex = new Mutex();
        var secondEntered = 0;
        using ManualResetEventSlim secondStarted = new(false);

        var scope = mutex.EnterScope();
        ValueTask second;
        try
        {
            second = Go.Run(async ct =>
            {
                secondStarted.Set();
                using (mutex.EnterScope())
                {
                    Interlocked.Exchange(ref secondEntered, 1);
                }
            }, cancellationToken: cancellation);

            AssertWait(secondStarted, "Timed out waiting for second mutex waiter to start.", cancellation);
            AssertIncomplete(second, ShortDelay, cancellation);
            Volatile.Read(ref secondEntered).ShouldBe(0);
        }
        finally
        {
            scope.Dispose();
        }

        await second.AsTask().WaitAsync(cancellation);
        secondEntered.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RwMutex_EnterReadScope_ShouldAllowConcurrentReadersAndBlockWriter()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var rwMutex = new RwMutex();
        using ManualResetEventSlim startReaders = new(false);
        object gate = new();
        int inside = 0;
        int maxInside = 0;

        void ReaderAction()
        {
            startReaders.Wait(WaitTimeout, cancellation).ShouldBeTrue("Timed out waiting to start readers.");
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

        ValueTask StartReader() => new(Task.Factory.StartNew(
            ReaderAction,
            cancellation,
            TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
            TaskScheduler.Default));

        ValueTask reader1 = StartReader();
        ValueTask reader2 = StartReader();
        startReaders.Set();
        await Task.WhenAll(reader1.AsTask(), reader2.AsTask());
        maxInside.ShouldBe(2);

        var readScope = rwMutex.EnterReadScope();
        using ManualResetEventSlim writerStarted = new(false);
        var writerEntered = 0;
        ValueTask writer = Go.Run(async ct =>
        {
            writerStarted.Set();
            using (rwMutex.EnterWriteScope())
            {
                Interlocked.Exchange(ref writerEntered, 1);
            }
            BusyWait(WriterDelay, cancellation);
        }, cancellationToken: cancellation);

        AssertWait(writerStarted, "Timed out waiting for writer to start.", cancellation);
        AssertIncomplete(writer, ShortDelay, cancellation);
        Volatile.Read(ref writerEntered).ShouldBe(0);
        readScope.Dispose();
        await writer.AsTask().WaitAsync(cancellation);
        writerEntered.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RwMutex_EnterWriteScope_ShouldBlockReadersUntilReleased()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var rwMutex = new RwMutex();
        var writeScope = rwMutex.EnterWriteScope();
        using ManualResetEventSlim readerStarted = new(false);
        var readerEntered = 0;

        ValueTask reader = Go.Run(async ct =>
        {
            readerStarted.Set();
            using (rwMutex.EnterReadScope())
            {
                Interlocked.Exchange(ref readerEntered, 1);
                BusyWait(WriterDelay, cancellation);
            }
        }, cancellationToken: cancellation);

        AssertWait(readerStarted, "Timed out waiting for reader to start.", cancellation);
        AssertIncomplete(reader, ShortDelay, cancellation);
        Volatile.Read(ref readerEntered).ShouldBe(0);

        writeScope.Dispose();
        await reader.AsTask().WaitAsync(cancellation);
        readerEntered.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public void Once_Do_ShouldThrow_WhenActionNull()
    {
        var once = new Once();
        Should.Throw<ArgumentNullException>(() => once.Do(null!));
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RwMutex_RLockAsync_ShouldReleaseWriterWhenLastReaderDisposes()
    {
        var cancellation = TestContext.Current.CancellationToken;
        using var rwMutex = new RwMutex();

        var firstReader = await rwMutex.RLockAsync(cancellation);
        var secondReader = await rwMutex.RLockAsync(cancellation);

        var writerEntered = 0;
        var writer = Go.Run(async ct =>
        {
            await using (await rwMutex.LockAsync(ct))
            {
                Interlocked.Exchange(ref writerEntered, 1);
            }
        }, cancellationToken: cancellation);

        writer.AsTask().IsCompleted.ShouldBeFalse();

        await firstReader.DisposeAsync();
        await secondReader.DisposeAsync();

        await writer.AsTask().WaitAsync(cancellation);
        writerEntered.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask RwMutex_RLockAsync_ShouldObserveCancellation()
    {
        using var cts = new CancellationTokenSource();
        using var rwMutex = new RwMutex();

        await using (await rwMutex.RLockAsync(TestContext.Current.CancellationToken))
        {
            cts.Cancel();
            var acquiring = rwMutex.RLockAsync(cts.Token);
            await Should.ThrowAsync<OperationCanceledException>(async () => await acquiring.AsTask());
        }
    }
}
