// Import the Hugo helpers to use them without the 'Hugo.' prefix.
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

[Collection("GoConcurrency")]
public partial class GoTests
{
    // This helper is used by tests but is not a test itself.
    private static Result<string> ReadFileContent(string path)
    {
        using (Defer(static () => Console.WriteLine("[ReadFileContent] Cleanup finished.")))
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Err<string>("Path cannot be null or empty.");
            }

            try
            {
                return Ok(File.ReadAllText(path));
            }
            catch (Exception ex)
            {
                return Err<string>(ex);
            }
        }
    }

    [Fact(Timeout = 15_000)]
    public void Defer_ShouldExecute_OnNormalCompletion()
    {
        var executed = false;

        using (Defer(() => executed = true))
        {
        }

        Assert.True(executed);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Defer_ShouldExecute_OnException()
    {
        var deferredActionExecuted = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
        {
            using (Defer(() => deferredActionExecuted = true))
            {
                throw new InvalidOperationException("Simulating an error.");
            }
        });
        Assert.True(deferredActionExecuted);
    }

    [Fact(Timeout = 15_000)]
    public async Task Mutex_ShouldThrow_WhenLockIsCancelled()
    {
        using var mutex = new Mutex();
        using var cts = new CancellationTokenSource();
        using (await mutex.LockAsync(cts.Token))
        {
            var waitingTask = mutex.LockAsync(cts.Token);
            await Task.Delay(50, cts.Token);
            await cts.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waitingTask);
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Mutex_LockAsync_WithPreCanceledToken_ShouldThrow()
    {
        using var mutex = new Mutex();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await mutex.LockAsync(cts.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task Mutex_ShouldThrowAfterDispose()
    {
        using var mutex = new Mutex();
        mutex.Dispose();

        Assert.Throws<ObjectDisposedException>(() => mutex.EnterScope());
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await mutex.LockAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task Mutex_ShouldNotBeReentrant()
    {
        using var mutex = new Mutex();
        using (await mutex.LockAsync(TestContext.Current.CancellationToken))
        {
            var reentrantLockTask = mutex.LockAsync(TestContext.Current.CancellationToken).AsTask();
            var completedTask = await Task.WhenAny(
                reentrantLockTask,
                Task.Delay(100, cancellationToken: TestContext.Current.CancellationToken)
            );
            Assert.NotEqual(reentrantLockTask, completedTask);
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Mutex_AsyncReleaser_ShouldBeIdempotent()
    {
        using var mutex = new Mutex();
        var releaser = await mutex.LockAsync(TestContext.Current.CancellationToken);

        await releaser.DisposeAsync();
        await releaser.DisposeAsync();

        using (await mutex.LockAsync(TestContext.Current.CancellationToken))
        {
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Mutex_AsyncReleaser_DisposeAsync_ShouldReleaseLock()
    {
        using var mutex = new Mutex();
        var releaser = await mutex.LockAsync(TestContext.Current.CancellationToken);

        await releaser.DisposeAsync();

        await using (await mutex.LockAsync(TestContext.Current.CancellationToken))
        {
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task Mutex_AsyncReleaser_DisposeAsyncTwice_ShouldNotThrow()
    {
        using var mutex = new Mutex();
        var releaser = await mutex.LockAsync(TestContext.Current.CancellationToken);

        await releaser.DisposeAsync();
        await releaser.DisposeAsync();
    }

    [Fact(Timeout = 15_000)]
    public async Task Once_ShouldExecuteAction_ExactlyOnce_Concurrently()
    {
        var once = new Once();
        var counter = 0;
        var wg = new WaitGroup();
        for (var i = 0; i < 10; i++)
        {
            var task = Task.Run(
                () =>
                {
                    once.Do(() => Interlocked.Increment(ref counter));
                },
                TestContext.Current.CancellationToken
            );
            wg.Add(task);
        }
        await wg.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, counter);
    }

    [Fact(Timeout = 15_000)]
    public void Pool_ShouldRecycleObjects()
    {
        var pool = new Pool<object> { New = static () => new object() };
        var obj1 = pool.Get();
        pool.Put(obj1);
        var obj2 = pool.Get();
        Assert.Same(obj1, obj2);
    }

    [Fact(Timeout = 15_000)]
    public void Pool_Get_ShouldThrow_WhenEmptyAndNoFactory()
    {
        var pool = new Pool<object>();
        Assert.Throws<InvalidOperationException>(() => pool.Get());
    }

    [Fact(Timeout = 15_000)]
    public async Task RWMutex_ShouldAllow_MultipleConcurrentReaders()
    {
        using var rwMutex = new RwMutex();
        var activeReaders = 0;
        var maxConcurrentReaders = 0;
        var wg = new WaitGroup();
        using var startSignal = new ManualResetEventSlim(false);

        for (var i = 0; i < 5; i++)
        {
            var task = Task.Run(
                async () =>
                {
                    Assert.True(startSignal.Wait(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken), "Timed out waiting to start RWMutex readers.");
                    using (await rwMutex.RLockAsync())
                    {
                        var currentReaders = Interlocked.Increment(ref activeReaders);
                        Interlocked.Exchange(
                            ref maxConcurrentReaders,
                            Math.Max(maxConcurrentReaders, currentReaders)
                        );
                        await Task.Delay(50, TestContext.Current.CancellationToken);
                        Interlocked.Decrement(ref activeReaders);
                    }
                },
                TestContext.Current.CancellationToken
            );
            wg.Add(task);
        }

        startSignal.Set();
        await wg.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(5, maxConcurrentReaders);
    }

    [Fact(Timeout = 15_000)]
    public async Task RWMutex_ShouldProvide_ExclusiveWriteLock()
    {
        using var rwMutex = new RwMutex();
        var writeLockHeld = false;
        var wg = new WaitGroup();

        var writerTask = Task.Run(
            async () =>
            {
                using (await rwMutex.LockAsync())
                {
                    writeLockHeld = true;
                    await Task.Delay(100, TestContext.Current.CancellationToken);
                    writeLockHeld = false;
                }
            },
            TestContext.Current.CancellationToken
        );
        wg.Add(writerTask);

        await Task.Delay(10, TestContext.Current.CancellationToken);

        var readerAttemptTask = Task.Run(
            async () =>
            {
                using (await rwMutex.RLockAsync())
                {
                    Assert.False(writeLockHeld);
                }
            },
            TestContext.Current.CancellationToken
        );
        wg.Add(readerAttemptTask);

        var writerAttemptTask = Task.Run(
            async () =>
            {
                using (await rwMutex.LockAsync())
                {
                    Assert.False(writeLockHeld);
                }
            },
            TestContext.Current.CancellationToken
        );
        wg.Add(writerAttemptTask);

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        Assert.False(writeLockHeld);
    }

    [Fact(Timeout = 15_000)]
    public void ReadFileContent_ShouldSucceed_WithValidFile()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "Hello from GoSharp!");
        var result = ReadFileContent(tempFile);
        Assert.True(result.IsSuccess);
        Assert.Equal("Hello from GoSharp!", result.Value);
        File.Delete(tempFile);
    }

    [Fact(Timeout = 15_000)]
    public void ReadFileContent_ShouldFail_WithInvalidFile()
    {
        var result = ReadFileContent("non_existent_file.txt");
        Assert.True(result.IsFailure);
        Assert.NotNull(result.Error);
        Assert.Contains("Could not find file", result.Error!.Message);
    }

    [Fact(Timeout = 15_000)]
    public async Task Concurrency_ShouldCommunicate_ViaChannel()
    {
        var channel = MakeChannel<string>();
        _ = Run(async () =>
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
            await channel.Writer.WriteAsync("Work complete!", TestContext.Current.CancellationToken);
        });
        var message = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Work complete!", message);
    }

    [Fact(Timeout = 15_000)]
    public async Task Concurrency_ShouldBlock_WhenBoundedChannelIsFull()
    {
        var channel = MakeChannel<int>(1);
        var writerTaskCompleted = false;
        await channel.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        var writerTask = Task.Run(
            async () =>
            {
                await channel.Writer.WriteAsync(2, TestContext.Current.CancellationToken);
                writerTaskCompleted = true;
            },
            TestContext.Current.CancellationToken
        );
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(writerTaskCompleted);
        var firstItem = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        await writerTask;
        var secondItem = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, firstItem);
        Assert.Equal(2, secondItem);
        Assert.True(writerTaskCompleted);
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitGroup_ShouldWait_ForAllTasksToComplete()
    {
        var wg = new WaitGroup();
        var counter = 0;
        for (var i = 0; i < 5; i++)
        {
            var task = Task.Run(
                async () =>
                {
                    await Task.Delay(20, TestContext.Current.CancellationToken);
                    Interlocked.Increment(ref counter);
                },
                TestContext.Current.CancellationToken
            );
            wg.Add(task);
        }
        await wg.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, counter);
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitGroup_ShouldCompleteImmediately_WhenNoTasksAreAdded()
    {
        var wg = new WaitGroup();
        await wg.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(true);
    }

    [Fact(Timeout = 15_000)]
    public async Task Mutex_ShouldPreventRaceConditions()
    {
        var wg = new WaitGroup();
        using var mutex = new Mutex();
        var counter = 0;
        const int numTasks = 5;
        const int incrementsPerTask = 10;
        for (var i = 0; i < numTasks; i++)
        {
            var task = Go.Run(
                async ct =>
                {
                    for (var j = 0; j < incrementsPerTask; j++)
                    {
                        using (await mutex.LockAsync(ct))
                        {
                            var currentValue = counter;
                            await Task.Delay(1, ct);
                            counter = currentValue + 1;
                        }
                    }
                },
                TestContext.Current.CancellationToken
            );
            wg.Add(task);
        }
        await wg.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(numTasks * incrementsPerTask, counter);
    }

    [Fact(Timeout = 15_000)]
    public void WaitGroup_Add_ShouldThrow_OnNonPositiveDelta()
    {
        var wg = new WaitGroup();

        Assert.Throws<ArgumentOutOfRangeException>(() => wg.Add(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => wg.Add(-1));
    }

    [Fact(Timeout = 15_000)]
    public void WaitGroup_AddTask_ShouldThrow_WhenTaskIsNull()
    {
        var wg = new WaitGroup();

        Assert.Throws<ArgumentNullException>(() => wg.Add((Task)null!));
    }

    [Fact(Timeout = 15_000)]
    public void WaitGroup_Go_ShouldThrow_WhenWorkIsNull()
    {
        var wg = new WaitGroup();

        Assert.Throws<ArgumentNullException>(() => wg.Go((Func<Task>)null!, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public void WaitGroup_Go_WithCancellationToken_ShouldThrow_WhenWorkIsNull()
    {
        var wg = new WaitGroup();

        Assert.Throws<ArgumentNullException>(() => wg.Go((Func<CancellationToken, Task>)null!, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public void WaitGroup_Done_ShouldThrow_WhenCalledTooManyTimes()
    {
        var wg = new WaitGroup();
        wg.Add(1);
        wg.Done();

        Assert.Throws<InvalidOperationException>(() => wg.Done());
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitGroup_WaitAsync_ShouldRespectCancellation()
    {
        var wg = new WaitGroup();
        wg.Add(1);

        using var cts = new CancellationTokenSource(20);
        var waitTask = wg.WaitAsync(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
        wg.Done();
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitGroup_AddTask_ShouldCompleteWhenTrackedTaskFinishes()
    {
        var wg = new WaitGroup();
        var tcs = new TaskCompletionSource();
        wg.Add(tcs.Task);

        tcs.SetResult();
        await wg.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, wg.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task Mutex_DisposeAsync_ShouldReleaseLock()
    {
        using var mutex = new Mutex();
        var releaser = await mutex.LockAsync(TestContext.Current.CancellationToken);

        var pending = mutex.LockAsync(TestContext.Current.CancellationToken);
        await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);

        await releaser.DisposeAsync();
        using (await pending)
        {
            Assert.True(true);
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task RwMutex_RLockAsync_ShouldCancelWhenWriterHeld()
    {
        using var rwMutex = new RwMutex();
        using (await rwMutex.LockAsync(TestContext.Current.CancellationToken))
        {
            using var cts = new CancellationTokenSource(20);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rwMutex.RLockAsync(cts.Token));
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task RwMutex_LockAsync_DisposeAsync_ShouldRelease()
    {
        using var rwMutex = new RwMutex();
        var writer = await rwMutex.LockAsync(TestContext.Current.CancellationToken);
        var pendingWriter = rwMutex.LockAsync(TestContext.Current.CancellationToken).AsTask();

        await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.False(pendingWriter.IsCompleted);

        await writer.DisposeAsync();
        using (await pendingWriter)
        {
            Assert.True(true);
        }
    }

    [Fact(Timeout = 15_000)]
    public void Pool_Put_ShouldThrowOnNull()
    {
        var pool = new Pool<object> { New = () => new object() };

        Assert.Throws<ArgumentNullException>(() => pool.Put(null!));
    }

    [Fact(Timeout = 15_000)]
    public async Task Run_ShouldExecuteDelegate()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Run(async () =>
        {
            tcs.SetResult();
            await Task.CompletedTask;
        });

        await tcs.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 15_000)]
    public async Task Run_ShouldThrowWhenFuncIsNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(static () => Run((Func<Task>)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(static () => Run((Func<CancellationToken, Task>)null!, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task Run_WithCancellationToken_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run(_ => Task.Delay(1000, _), cts.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task Run_ShouldAcceptExistingTask()
    {
        var existing = Task.Delay(10, TestContext.Current.CancellationToken);
        var tracked = Run(existing);

        Assert.Same(existing, tracked);
        await tracked;
    }

    [Fact(Timeout = 15_000)]
    public async Task Run_ShouldAcceptExistingValueTask()
    {
        var invoked = 0;
        async ValueTask WorkAsync()
        {
            await Task.Yield();
            Interlocked.Increment(ref invoked);
        }

        var tracked = Run(WorkAsync());
        await tracked;

        Assert.Equal(1, invoked);
    }

    [Fact(Timeout = 15_000)]
    public async Task Run_ShouldRespectCustomScheduler()
    {
        var scheduler = new InlineTaskScheduler();
        var executedOnScheduler = 0;

        await Run(() =>
        {
            if (TaskScheduler.Current == scheduler)
            {
                Interlocked.Increment(ref executedOnScheduler);
            }

            return Task.CompletedTask;
        }, scheduler: scheduler);

        Assert.Equal(1, executedOnScheduler);
        Assert.Equal(1, scheduler.ExecutionCount);
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitGroup_Go_ShouldRespectCustomScheduler()
    {
        var wg = new WaitGroup();
        var scheduler = new InlineTaskScheduler();
        var executedOnScheduler = 0;

        wg.Go(() =>
        {
            if (TaskScheduler.Current == scheduler)
            {
                Interlocked.Increment(ref executedOnScheduler);
            }
            return Task.CompletedTask;
        }, cancellationToken: TestContext.Current.CancellationToken, scheduler: scheduler);

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, executedOnScheduler);
        Assert.Equal(1, scheduler.ExecutionCount);
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitGroup_Go_ShouldAcceptExistingTask()
    {
        var wg = new WaitGroup();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        wg.Go(tcs.Task);
        Assert.Equal(1, wg.Count);

        tcs.TrySetResult();
        await wg.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, wg.Count);
    }

    [Fact(Timeout = 15_000)]
    public async Task DelayAsync_WithFakeTimeProvider_ShouldCompleteWhenAdvanced()
    {
        var provider = new FakeTimeProvider();

        var delayTask = Go.DelayAsync(TimeSpan.FromSeconds(5), provider, TestContext.Current.CancellationToken);

        Assert.False(delayTask.IsCompleted);

        provider.Advance(TimeSpan.FromSeconds(5));

        await delayTask.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 15_000)]
    public async Task DelayAsync_ShouldThrow_WhenDelayIsNegative() => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(static () =>
                                                                               DelayAsync(TimeSpan.FromMilliseconds(-2), cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task DelayAsync_ShouldRespectCancellation()
    {
        using var cts = new CancellationTokenSource();
        var provider = new FakeTimeProvider();
        var delayTask = Go.DelayAsync(TimeSpan.FromSeconds(5), provider, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await delayTask);
    }

    [Fact(Timeout = 15_000)]
    public void ChannelCase_Create_ShouldThrow_WhenReaderIsNull() => Assert.Throws<ArgumentNullException>(static () => ChannelCase.Create<int>(null!, static (_, _) => ValueTask.FromResult(Result.Ok(Go.Unit.Value))));

    [Fact(Timeout = 15_000)]
    public void ChannelCase_Create_ShouldThrow_WhenContinuationIsNull()
    {
        var channel = MakeChannel<int>();

        Assert.Throws<ArgumentNullException>(() => ChannelCase.Create(channel.Reader, (Func<int, CancellationToken, ValueTask<Result<Go.Unit>>>)null!));
    }

    [Fact(Timeout = 15_000)]
    public void ChannelCase_CreateAction_ShouldThrow_WhenActionIsNull()
    {
        var channel = MakeChannel<int>();

        Assert.Throws<ArgumentNullException>(() => ChannelCase.Create(channel.Reader, (Action<int>)null!));
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldThrow_WhenCasesNull() => await Assert.ThrowsAsync<ArgumentNullException>(static () => SelectAsync<Go.Unit>(cancellationToken: TestContext.Current.CancellationToken, cases: null!));

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldThrow_WhenCasesEmpty() => await Assert.ThrowsAsync<ArgumentException>(static () => SelectAsync<Go.Unit>(cancellationToken: TestContext.Current.CancellationToken, cases: []));

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldThrow_WhenTimeoutIsNegative()
    {
        var channel = MakeChannel<int>();
        var @case = ChannelCase.Create(channel.Reader, (_, _) => ValueTask.FromResult(Result.Ok(Go.Unit.Value)));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SelectAsync<Go.Unit>(TimeSpan.FromMilliseconds(-5), cancellationToken: TestContext.Current.CancellationToken, cases: [@case]));
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldReturnFailure_WhenCasesCompleteWithoutValue()
    {
        var channel = MakeChannel<int>();
        channel.Writer.TryComplete();

        var result = await SelectAsync<Go.Unit>(cancellationToken: TestContext.Current.CancellationToken, cases: [ChannelCase.Create(channel.Reader, static (_, _) => ValueTask.FromResult(Result.Ok(Unit.Value)))]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.SelectDrained, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldInvokeDefaultCase()
    {
        var invoked = false;

        var result = await SelectAsync<Go.Unit>(
            cancellationToken: TestContext.Current.CancellationToken,
            cases:
            [
                ChannelCase.CreateDefault(ct =>
                {
                    invoked = true;
                    Assert.True(ct.CanBeCanceled);
                    return ValueTask.FromResult(Result.Ok(Unit.Value));
                })
            ]);

        Assert.True(invoked);
        Assert.True(result.IsSuccess);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldPropagateDefaultFailure()
    {
        var result = await SelectAsync<Go.Unit>(
            cancellationToken: TestContext.Current.CancellationToken,
            cases:
            [
                ChannelCase.CreateDefault(static () => Result.Fail<Unit>(Error.From("default failure", ErrorCodes.Validation)))
            ]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
        Assert.Equal("default failure", result.Error?.Message);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldPreferReadyCaseOverDefault()
    {
        var channel = MakeChannel<int>();
        channel.Writer.TryWrite(42);
        channel.Writer.TryComplete();
        var defaultInvoked = false;

        var result = await SelectAsync<Go.Unit>(
            cancellationToken: TestContext.Current.CancellationToken,
            cases:
            [
                ChannelCase.CreateDefault(() =>
                {
                    defaultInvoked = true;
                    return Result.Ok(Unit.Value);
                }),
                ChannelCase.Create(channel.Reader, value =>
                {
                    Assert.Equal(42, value);
                    return ValueTask.FromResult(Result.Ok(Unit.Value));
                })
            ]);

        Assert.True(result.IsSuccess);
        Assert.False(defaultInvoked);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldThrow_WhenMultipleDefaultCasesSupplied() => await Assert.ThrowsAsync<ArgumentException>(static () =>
                                                                                             SelectAsync<Go.Unit>(
                                                                                                 cancellationToken: TestContext.Current.CancellationToken,
                                                                                                 cases:
                                                                                                 [
                                                                                                     ChannelCase.CreateDefault(static () => Result.Ok(Unit.Value)),
                    ChannelCase.CreateDefault(static () => Result.Ok(Unit.Value))
                                                                                                 ]));

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldWrapContinuationExceptions()
    {
        var channel = MakeChannel<int>();
        channel.Writer.TryWrite(42);
        channel.Writer.TryComplete();

        var result = await SelectAsync<Go.Unit>(cancellationToken: TestContext.Current.CancellationToken, cases: [ChannelCase.Create(channel.Reader, (Action<int>)(static _ => throw new InvalidOperationException("boom")))]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Contains("boom", result.Error?.Message);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldReturnFirstCompletedCase()
    {
        var channel1 = MakeChannel<int>();
        var channel2 = MakeChannel<int>();
        var firstExecuted = false;
        var secondExecuted = false;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var selectTask = SelectAsync<Go.Unit>(
            cancellationToken: cts.Token,
            cases:
            [
                ChannelCase.Create(channel1.Reader, (value, _) =>
                {
                    firstExecuted = true;
                    Assert.Equal(42, value);
                    return ValueTask.FromResult(Result.Ok(Go.Unit.Value));
                }),
                ChannelCase.Create(channel2.Reader, (value, _) =>
                {
                    secondExecuted = true;
                    return ValueTask.FromResult(Result.Ok(Go.Unit.Value));
                })
            ]);

        await channel1.Writer.WriteAsync(42, cts.Token);
        channel1.Writer.TryComplete();
        channel2.Writer.TryComplete();

        var result = await selectTask;

        Assert.True(result.IsSuccess);
        Assert.True(firstExecuted);
        Assert.False(secondExecuted);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldReturnTimeoutError_WhenDeadlineExpires()
    {
        var channel = MakeChannel<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var provider = new FakeTimeProvider();

        var selectTask = SelectAsync<Go.Unit>(
            timeout: TimeSpan.FromSeconds(1),
            provider: provider,
            cancellationToken: cts.Token,
            cases:
            [
                ChannelCase.Create(channel.Reader, static (_, _) => ValueTask.FromResult(Result.Ok(Go.Unit.Value)))
            ]);

        Assert.False(selectTask.IsCompleted);

        provider.Advance(TimeSpan.FromSeconds(1));

        var result = await selectTask;

        channel.Writer.TryComplete();

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Timeout, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldRespectCancellation()
    {
        var channel = MakeChannel<int>();
        using var cts = new CancellationTokenSource();

        var selectTask = SelectAsync<Go.Unit>(
            cancellationToken: cts.Token,
            cases: [ChannelCase.Create(channel.Reader, static (_, _) => ValueTask.FromResult(Result.Ok(Go.Unit.Value)))]);

        cts.CancelAfter(20);

        var result = await selectTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        Assert.True(result.Error!.TryGetMetadata("cancellationToken", out CancellationToken recordedToken));
        Assert.Equal(cts.Token, recordedToken);
        channel.Writer.TryComplete();
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldEmitActivityTelemetry_OnSuccess()
    {
        GoDiagnostics.Reset();
        var stoppedActivities = new List<Activity>();

        try
        {
            using var source = new ActivitySource("Hugo.Go.Tests", "1.0.0");
            GoDiagnostics.Configure(source);
            using var listener = CreateSelectActivityListener(source.Name, stoppedActivities);

            var channel = MakeChannel<int>();
            channel.Writer.TryWrite(7);
            channel.Writer.TryComplete();

            var result = await SelectAsync<Go.Unit>(
                cancellationToken: TestContext.Current.CancellationToken,
                cases:
                [
                    ChannelCase.Create(channel.Reader, static (_, _) => ValueTask.FromResult(Result.Ok(Unit.Value)))
                ]);

            Assert.True(result.IsSuccess);

            Activity[] recorded;
            lock (stoppedActivities)
            {
                recorded = [.. stoppedActivities];
            }

            var activity = Assert.Single(recorded, static activity => activity.DisplayName == "Go.Select");
            Assert.Equal("Go.Select", activity.DisplayName);
            Assert.Equal(ActivityStatusCode.Ok, activity.Status);
            Assert.Equal(1, activity.GetTagItem("hugo.select.case_count"));
            Assert.Equal("completed", activity.GetTagItem("hugo.select.outcome"));
            Assert.NotNull(activity.GetTagItem("hugo.select.duration_ms"));
        }
        finally
        {
            GoDiagnostics.Reset();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectAsync_ShouldEmitActivityTelemetry_OnFailure()
    {
        GoDiagnostics.Reset();
        var stoppedActivities = new List<Activity>();

        try
        {
            using var source = new ActivitySource("Hugo.Go.Tests", "1.0.0");
            GoDiagnostics.Configure(source);
            using var listener = CreateSelectActivityListener(source.Name, stoppedActivities);

            var channel = MakeChannel<int>();
            channel.Writer.TryWrite(13);
            channel.Writer.TryComplete();

            var result = await SelectAsync<Go.Unit>(
                cancellationToken: TestContext.Current.CancellationToken,
                cases:
                [
                    ChannelCase.Create(channel.Reader, static (_, _) => ValueTask.FromResult(Result.Fail<Unit>(Error.From("failure", "error.activity"))))
                ]);

            Assert.True(result.IsFailure);
            Assert.Equal("error.activity", result.Error?.Code);

            Activity[] recorded;
            lock (stoppedActivities)
            {
                recorded = [.. stoppedActivities];
            }

            var activity = Assert.Single(recorded, static activity => activity.DisplayName == "Go.Select");
            Assert.Equal(ActivityStatusCode.Error, activity.Status);
            Assert.Equal("error", activity.GetTagItem("hugo.select.outcome"));
            Assert.Equal("error.activity", activity.GetTagItem("hugo.error.code"));
        }
        finally
        {
            GoDiagnostics.Reset();
        }
    }

    [Fact(Timeout = 15_000)]
    public async Task FakeTimeProviderDelay_ShouldCompleteAfterAdvance()
    {
        var provider = new FakeTimeProvider();

        var delayTask = Go.DelayAsync(TimeSpan.FromSeconds(1), provider, TestContext.Current.CancellationToken);

        provider.Advance(TimeSpan.FromSeconds(2));

        await delayTask;
    }

    [Theory]
    [InlineData(new[] { 1, 2 })]
    public async Task SelectAsync_ShouldSupportRepeatedInvocations(int[] expected)
    {
        var channel = MakeChannel<int>();
        var observed = new List<int>();

        var cases = new[]
        {
            ChannelCase.Create(channel.Reader, (value, _) =>
            {
                observed.Add(value);
                return ValueTask.FromResult(Result.Ok(Unit.Value));
            })
        };

        await channel.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        var first = await SelectAsync<Go.Unit>(cancellationToken: TestContext.Current.CancellationToken, cases: cases);
        Assert.True(first.IsSuccess);

        await channel.Writer.WriteAsync(2, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var second = await SelectAsync<Go.Unit>(cancellationToken: TestContext.Current.CancellationToken, cases: cases);

        Assert.True(second.IsSuccess);
        Assert.Equal(expected, observed);
    }

    [Theory]
    [InlineData(new[] { 99 })]
    public async Task ChannelCaseTemplates_With_ShouldMaterializeCases(int[] expected)
    {
        var channel1 = MakeChannel<int>();
        var channel2 = MakeChannel<int>();
        var observed = new List<int>();

        var templates = new[]
        {
            ChannelCaseTemplates.From(channel1.Reader),
            ChannelCaseTemplates.From(channel2.Reader)
        };

        var cases = templates.With<int, Go.Unit>((value, _) =>
        {
            observed.Add(value);
            return ValueTask.FromResult(Result.Ok(Unit.Value));
        });

        var selectTask = SelectAsync<Go.Unit>(cancellationToken: TestContext.Current.CancellationToken, cases: cases);

        await channel2.Writer.WriteAsync(99, TestContext.Current.CancellationToken);
        channel1.Writer.TryComplete();
        channel2.Writer.TryComplete();

        var result = await selectTask;

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, observed);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectBuilder_ShouldReturnProjectedResult_WhenTemplateUsed()
    {
        var channel = MakeChannel<int>();
        var template = ChannelCaseTemplates.From(channel.Reader);

        var selectTask = Select<string>(cancellationToken: TestContext.Current.CancellationToken)
            .Case(template, static value => $"value:{value}")
            .ExecuteAsync();

        await channel.Writer.WriteAsync(7, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var result = await selectTask;

        Assert.True(result.IsSuccess);
        Assert.Equal("value:7", result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectBuilder_ShouldPropagateCaseFailure()
    {
        var channel = MakeChannel<int>();

        var selectTask = Select<int>(cancellationToken: TestContext.Current.CancellationToken)
            .Case(channel.Reader, static (_, _) => Task.FromResult(Result.Fail<int>(Error.From("boom", ErrorCodes.Validation))))
            .ExecuteAsync();

        await channel.Writer.WriteAsync(5, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var result = await selectTask;

        Assert.True(result.IsFailure);
        Assert.Equal("boom", result.Error?.Message);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectBuilder_ShouldPropagateTimeout()
    {
        var provider = new FakeTimeProvider();
        var channel = MakeChannel<int>();

        var selectTask = Select<string>(TimeSpan.FromSeconds(1), provider, TestContext.Current.CancellationToken)
            .Case(channel.Reader, static value => value.ToString())
            .ExecuteAsync();

        provider.Advance(TimeSpan.FromSeconds(1));

        var result = await selectTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Timeout, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectBuilder_ShouldThrow_WhenNoCasesConfigured()
    {
        var builder = Select<int>(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.ExecuteAsync());
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectBuilder_ShouldReturnDefaultResult_WhenNoCasesConfigured()
    {
        var result = await Select<string>(cancellationToken: TestContext.Current.CancellationToken)
            .Default(static () => "fallback")
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("fallback", result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectBuilder_ShouldPreferReadyCasesOverDefault()
    {
        var channel = MakeChannel<int>();
        channel.Writer.TryWrite(9);
        channel.Writer.TryComplete();

        var result = await Select<int>(cancellationToken: TestContext.Current.CancellationToken)
            .Case(channel.Reader, static value => value)
            .Default(static () => 5)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(9, result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectBuilder_ShouldHonorPriorityOrdering_WhenMultipleCasesReady()
    {
        var highPriority = MakeChannel<int>();
        var lowPriority = MakeChannel<int>();

        highPriority.Writer.TryWrite(1);
        lowPriority.Writer.TryWrite(2);
        highPriority.Writer.TryComplete();
        lowPriority.Writer.TryComplete();

        var result = await Select<int>(cancellationToken: TestContext.Current.CancellationToken)
            .Case(lowPriority.Reader, static value => value)
            .Case(highPriority.Reader, priority: -1, static value => value)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(1, result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectBuilder_ShouldHonorRegistrationOrder_WhenPrioritiesMatch()
    {
        var first = MakeChannel<int>();
        var second = MakeChannel<int>();

        first.Writer.TryWrite(7);
        second.Writer.TryWrite(9);
        first.Writer.TryComplete();
        second.Writer.TryComplete();

        var result = await Select<int>(cancellationToken: TestContext.Current.CancellationToken)
            .Case(first.Reader, static value => value)
            .Case(second.Reader, static value => value)
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectBuilder_ShouldInvokeDefault_WhenCasesDrainWithoutValues()
    {
        var channel = MakeChannel<int>();
        channel.Writer.TryComplete();

        var result = await Select<string>(cancellationToken: TestContext.Current.CancellationToken)
            .Case(channel.Reader, static value => value.ToString())
            .Default(static () => "fallback")
            .ExecuteAsync();

        Assert.True(result.IsSuccess);
        Assert.Equal("fallback", result.Value);
    }

    [Fact(Timeout = 15_000)]
    public void SelectBuilder_Default_ShouldThrow_WhenConfiguredTwice()
    {
        var builder = Select<int>(cancellationToken: TestContext.Current.CancellationToken)
            .Default(() => 1);

        Assert.Throws<InvalidOperationException>(() => builder.Default(() => 2));
    }

    [Fact(Timeout = 15_000)]
    public async Task SelectBuilder_Deadline_ShouldYieldConfiguredResult()
    {
        var provider = new FakeTimeProvider();

        var selectTask = Select<string>(provider: provider, cancellationToken: TestContext.Current.CancellationToken)
            .Deadline(TimeSpan.FromSeconds(1), static () => "expired")
            .ExecuteAsync();

        provider.Advance(TimeSpan.FromSeconds(1));

        var result = await selectTask;

        Assert.True(result.IsSuccess);
        Assert.Equal("expired", result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitGroup_WaitAsync_WithFakeTimeProvider_ShouldReturnFalseWhenIncomplete()
    {
        var provider = new FakeTimeProvider();
        var wg = new WaitGroup();
        var tcs = new TaskCompletionSource();
        wg.Add(tcs.Task);

        var waitTask = wg.WaitAsync(TimeSpan.FromSeconds(1), provider, TestContext.Current.CancellationToken);

        Assert.False(waitTask.IsCompleted);

        provider.Advance(TimeSpan.FromSeconds(1));

        var timedOut = await waitTask;

        Assert.False(timedOut);

        tcs.SetResult();
        await wg.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitGroup_WaitAsync_WithFakeTimeProvider_ShouldReturnTrueWhenAllComplete()
    {
        var provider = new FakeTimeProvider();
        var wg = new WaitGroup();
        var tcs = new TaskCompletionSource();
        wg.Add(tcs.Task);

        var waitTask = wg.WaitAsync(TimeSpan.FromSeconds(5), provider, TestContext.Current.CancellationToken);

        Assert.False(waitTask.IsCompleted);

        tcs.SetResult();

        var completed = await waitTask;

        Assert.True(completed);
        await wg.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithZeroCapacity_ShouldCreateUnboundedChannel()
    {
        var channel = MakeChannel<int>(0);

        Assert.True(channel.Writer.TryWrite(1));
        Assert.True(channel.Writer.TryWrite(2));
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithNullBoundedOptions_ShouldThrow() => Assert.Throws<ArgumentNullException>(static () => MakeChannel<int>((BoundedChannelOptions)null!));

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithNullUnboundedOptions_ShouldThrow() => Assert.Throws<ArgumentNullException>(static () => MakeChannel<int>((UnboundedChannelOptions)null!));

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithNullPrioritizedOptions_ShouldThrow() => Assert.Throws<ArgumentNullException>(static () => MakeChannel<int>((PrioritizedChannelOptions)null!));

    [Fact(Timeout = 15_000)]
    public void MakePrioritizedChannel_ShouldThrow_WhenPriorityLevelsInvalid() => Assert.Throws<ArgumentOutOfRangeException>(static () => MakePrioritizedChannel<int>(0));

    [Fact(Timeout = 15_000)]
    public void MakePrioritizedChannel_ShouldThrow_WhenDefaultPriorityTooHigh() => Assert.Throws<ArgumentOutOfRangeException>(static () => MakePrioritizedChannel<int>(priorityLevels: 2, defaultPriority: 2));

    private static readonly int[] expected = [2, 3, 1];

    [Fact(Timeout = 15_000)]
    public async Task MakeChannel_WithPrioritizedOptions_ShouldYieldHigherPriorityFirst()
    {
        var channel = MakeChannel<int>(new PrioritizedChannelOptions { PriorityLevels = 3 });
        var writer = channel.PrioritizedWriter;
        var reader = channel.Reader;

        await writer.WriteAsync(1, priority: 2, TestContext.Current.CancellationToken);
        await writer.WriteAsync(2, priority: 0, TestContext.Current.CancellationToken);
        await writer.WriteAsync(3, priority: 1, TestContext.Current.CancellationToken);
        writer.TryComplete();

        var results = new List<int>
        {
            await reader.ReadAsync(TestContext.Current.CancellationToken),
            await reader.ReadAsync(TestContext.Current.CancellationToken),
            await reader.ReadAsync(TestContext.Current.CancellationToken)
        };

        Assert.Equal(expected, results);
    }

    [Fact(Timeout = 15_000)]
    public async Task MakePrioritizedChannel_ShouldApplyDefaultPriority()
    {
        var channel = MakePrioritizedChannel<int>(priorityLevels: 2, defaultPriority: 0);
        var writer = channel.PrioritizedWriter;
        var reader = channel.Reader;

        await writer.WriteAsync(1, TestContext.Current.CancellationToken);
        await writer.WriteAsync(2, priority: 1, TestContext.Current.CancellationToken);
        writer.TryComplete();

        var first = await reader.ReadAsync(TestContext.Current.CancellationToken);
        var second = await reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
    }

    [Fact(Timeout = 15_000)]
    public async Task PrioritizedChannelWriter_ShouldRejectInvalidPriority()
    {
        var channel = MakeChannel<int>(new PrioritizedChannelOptions { PriorityLevels = 2 });
        var writer = channel.PrioritizedWriter;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await writer.WriteAsync(42, priority: 5, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public void Err_WithMessage_ShouldReturnFailure()
    {
        var result = Err<int>("message", ErrorCodes.Validation);

        Assert.True(result.IsFailure);
        Assert.Equal("message", result.Error?.Message);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }


    private static ActivityListener CreateSelectActivityListener(string sourceName, List<Activity> stoppedActivities)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                lock (stoppedActivities)
                {
                    stoppedActivities.Add(activity);
                }
            }
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class InlineTaskScheduler : TaskScheduler
    {
        private int _executionCount;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref _executionCount);
            TryExecuteTask(task);
        }

        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
        {
            Interlocked.Increment(ref _executionCount);
            return TryExecuteTask(task);
        }

        protected override IEnumerable<Task>? GetScheduledTasks() => null;
    }
}
