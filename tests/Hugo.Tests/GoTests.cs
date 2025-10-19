// Import the Hugo helpers to use them without the 'Hugo.' prefix.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Hugo;
using Microsoft.Extensions.Time.Testing;
using static Hugo.Go;

namespace Hugo.Tests;

public class GoTests
{
    // This helper is used by tests but is not a test itself.
    private static Result<string> ReadFileContent(string path)
    {
        using (Defer(() => Console.WriteLine("[ReadFileContent] Cleanup finished.")))
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

    [Fact]
    public void Defer_ShouldExecute_OnNormalCompletion()
    {
        var executed = false;

        using (Defer(() => executed = true))
        {
        }

        Assert.True(executed);
    }

    [Fact]
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

    [Fact]
    public async Task Mutex_ShouldThrow_WhenLockIsCancelled()
    {
    var mutex = new Mutex();
        var cts = new CancellationTokenSource();
        using (await mutex.LockAsync(cts.Token))
        {
            var waitingTask = mutex.LockAsync(cts.Token);
            await Task.Delay(50, cts.Token);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waitingTask);
        }
    }

    [Fact]
    public async Task Mutex_ShouldNotBeReentrant()
    {
    var mutex = new Mutex();
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

    [Fact]
    public async Task Mutex_AsyncReleaser_ShouldBeIdempotent()
    {
        var mutex = new Mutex();
        var releaser = await mutex.LockAsync(TestContext.Current.CancellationToken);

        releaser.Dispose();
        releaser.Dispose();

        using (await mutex.LockAsync(TestContext.Current.CancellationToken))
        {
        }
    }

    [Fact]
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

    [Fact]
    public void Pool_ShouldRecycleObjects()
    {
        var pool = new Pool<object> { New = () => new object() };
        var obj1 = pool.Get();
        pool.Put(obj1);
        var obj2 = pool.Get();
        Assert.Same(obj1, obj2);
    }

    [Fact]
    public void Pool_Get_ShouldThrow_WhenEmptyAndNoFactory()
    {
        var pool = new Pool<object>();
        Assert.Throws<InvalidOperationException>(() => pool.Get());
    }

    [Fact]
    public async Task RWMutex_ShouldAllow_MultipleConcurrentReaders()
    {
        var rwMutex = new RwMutex();
        var activeReaders = 0;
        var maxConcurrentReaders = 0;
        var wg = new WaitGroup();
        var startSignal = new ManualResetEventSlim(false);

        for (var i = 0; i < 5; i++)
        {
            var task = Task.Run(
                async () =>
                {
                    startSignal.Wait();
                    using (await rwMutex.RLockAsync())
                    {
                        var currentReaders = Interlocked.Increment(ref activeReaders);
                        Interlocked.Exchange(
                            ref maxConcurrentReaders,
                            Math.Max(maxConcurrentReaders, currentReaders)
                        );
                        await Task.Delay(50);
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

    [Fact]
    public async Task RWMutex_ShouldProvide_ExclusiveWriteLock()
    {
        var rwMutex = new RwMutex();
        var writeLockHeld = false;
        var wg = new WaitGroup();

        var writerTask = Task.Run(
            async () =>
            {
                using (await rwMutex.LockAsync())
                {
                    writeLockHeld = true;
                    await Task.Delay(100);
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

    [Fact]
    public void ReadFileContent_ShouldSucceed_WithValidFile()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "Hello from GoSharp!");
        var result = ReadFileContent(tempFile);
        Assert.True(result.IsSuccess);
        Assert.Equal("Hello from GoSharp!", result.Value);
        File.Delete(tempFile);
    }

    [Fact]
    public void ReadFileContent_ShouldFail_WithInvalidFile()
    {
        var result = ReadFileContent("non_existent_file.txt");
        Assert.True(result.IsFailure);
        Assert.NotNull(result.Error);
        Assert.Contains("Could not find file", result.Error!.Message);
    }

    [Fact]
    public async Task Concurrency_ShouldCommunicate_ViaChannel()
    {
        var channel = MakeChannel<string>();
        _ = Run(async () =>
        {
            await Task.Delay(100);
            await channel.Writer.WriteAsync("Work complete!");
        });
        var message = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Work complete!", message);
    }

    [Fact]
    public async Task Concurrency_ShouldBlock_WhenBoundedChannelIsFull()
    {
        var channel = MakeChannel<int>(1);
        var writerTaskCompleted = false;
        await channel.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        var writerTask = Task.Run(
            async () =>
            {
                await channel.Writer.WriteAsync(2);
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

    [Fact]
    public async Task WaitGroup_ShouldWait_ForAllTasksToComplete()
    {
        var wg = new WaitGroup();
        var counter = 0;
        for (var i = 0; i < 5; i++)
        {
            var task = Task.Run(
                async () =>
                {
                    await Task.Delay(20);
                    Interlocked.Increment(ref counter);
                },
                TestContext.Current.CancellationToken
            );
            wg.Add(task);
        }
        await wg.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(5, counter);
    }

    [Fact]
    public async Task WaitGroup_ShouldCompleteImmediately_WhenNoTasksAreAdded()
    {
        var wg = new WaitGroup();
        await wg.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(true);
    }

    [Fact]
    public async Task Mutex_ShouldPreventRaceConditions()
    {
        var wg = new WaitGroup();
    var mutex = new Mutex();
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

    [Fact]
    public void WaitGroup_Add_ShouldThrow_OnNonPositiveDelta()
    {
        var wg = new WaitGroup();

        Assert.Throws<ArgumentOutOfRangeException>(() => wg.Add(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => wg.Add(-1));
    }

    [Fact]
    public void WaitGroup_AddTask_ShouldThrow_WhenTaskIsNull()
    {
        var wg = new WaitGroup();

        Assert.Throws<ArgumentNullException>(() => wg.Add((Task)null!));
    }

    [Fact]
    public void WaitGroup_Go_ShouldThrow_WhenWorkIsNull()
    {
        var wg = new WaitGroup();

    Assert.Throws<ArgumentNullException>(() => wg.Go((Func<Task>)null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void WaitGroup_Go_WithCancellationToken_ShouldThrow_WhenWorkIsNull()
    {
        var wg = new WaitGroup();

        Assert.Throws<ArgumentNullException>(() => wg.Go((Func<CancellationToken, Task>)null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void WaitGroup_Done_ShouldThrow_WhenCalledTooManyTimes()
    {
        var wg = new WaitGroup();
        wg.Add(1);
        wg.Done();

        Assert.Throws<InvalidOperationException>(() => wg.Done());
    }

    [Fact]
    public async Task WaitGroup_WaitAsync_ShouldRespectCancellation()
    {
        var wg = new WaitGroup();
        wg.Add(1);

        using var cts = new CancellationTokenSource(20);
        var waitTask = wg.WaitAsync(cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
        wg.Done();
    }

    [Fact]
    public async Task WaitGroup_AddTask_ShouldCompleteWhenTrackedTaskFinishes()
    {
        var wg = new WaitGroup();
        var tcs = new TaskCompletionSource();
        wg.Add(tcs.Task);

        tcs.SetResult();
        await wg.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, wg.Count);
    }

    [Fact]
    public async Task Mutex_DisposeAsync_ShouldReleaseLock()
    {
    var mutex = new Mutex();
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

    [Fact]
    public async Task RwMutex_RLockAsync_ShouldCancelWhenWriterHeld()
    {
        var rwMutex = new RwMutex();
        using (await rwMutex.LockAsync(TestContext.Current.CancellationToken))
        {
            using var cts = new CancellationTokenSource(20);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await rwMutex.RLockAsync(cts.Token));
        }
    }

    [Fact]
    public async Task RwMutex_LockAsync_DisposeAsync_ShouldRelease()
    {
        var rwMutex = new RwMutex();
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

    [Fact]
    public void Pool_Put_ShouldThrowOnNull()
    {
        var pool = new Pool<object> { New = () => new object() };

        Assert.Throws<ArgumentNullException>(() => pool.Put(null!));
    }

    [Fact]
    public async Task Run_ShouldExecuteDelegate()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Go.Run(async () =>
        {
            tcs.SetResult();
            await Task.CompletedTask;
        });

        await tcs.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Run_ShouldThrowWhenFuncIsNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Go.Run((Func<Task>)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => Go.Run((Func<CancellationToken, Task>)null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Run_WithCancellationToken_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Go.Run(_ => Task.Delay(1000, _), cts.Token));
    }

    [Fact]
    public async Task DelayAsync_WithFakeTimeProvider_ShouldCompleteWhenAdvanced()
    {
        var provider = new FakeTimeProvider();

        var delayTask = Go.DelayAsync(TimeSpan.FromSeconds(5), provider, TestContext.Current.CancellationToken);

        Assert.False(delayTask.IsCompleted);

        provider.Advance(TimeSpan.FromSeconds(5));

        await delayTask.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DelayAsync_ShouldThrow_WhenDelayIsNegative() => await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                                                                               Go.DelayAsync(TimeSpan.FromMilliseconds(-2), cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public async Task DelayAsync_ShouldRespectCancellation()
    {
        using var cts = new CancellationTokenSource();
        var provider = new FakeTimeProvider();
        var delayTask = Go.DelayAsync(TimeSpan.FromSeconds(5), provider, cts.Token);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await delayTask);
    }

    [Fact]
    public void ChannelCase_Create_ShouldThrow_WhenReaderIsNull() => Assert.Throws<ArgumentNullException>(() => ChannelCase.Create<int>(null!, (_, _) => Task.FromResult(Result.Ok(Go.Unit.Value))));

    [Fact]
    public void ChannelCase_Create_ShouldThrow_WhenContinuationIsNull()
    {
        var channel = MakeChannel<int>();

        Assert.Throws<ArgumentNullException>(() => ChannelCase.Create(channel.Reader, (Func<int, CancellationToken, Task<Result<Go.Unit>>>)null!));
    }

    [Fact]
    public void ChannelCase_CreateAction_ShouldThrow_WhenActionIsNull()
    {
        var channel = MakeChannel<int>();

        Assert.Throws<ArgumentNullException>(() => ChannelCase.Create(channel.Reader, (Action<int>)null!));
    }

    [Fact]
    public async Task SelectAsync_ShouldThrow_WhenCasesNull() => await Assert.ThrowsAsync<ArgumentNullException>(() => SelectAsync(cancellationToken: TestContext.Current.CancellationToken, cases: null!));

    [Fact]
    public async Task SelectAsync_ShouldThrow_WhenCasesEmpty() => await Assert.ThrowsAsync<ArgumentException>(() => SelectAsync(cancellationToken: TestContext.Current.CancellationToken, cases: []));

    [Fact]
    public async Task SelectAsync_ShouldThrow_WhenTimeoutIsNegative()
    {
        var channel = MakeChannel<int>();
        var @case = ChannelCase.Create(channel.Reader, (_, _) => Task.FromResult(Result.Ok(Go.Unit.Value)));

    await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => SelectAsync(TimeSpan.FromMilliseconds(-5), cancellationToken: TestContext.Current.CancellationToken, cases: [@case]));
    }

    [Fact]
    public async Task SelectAsync_ShouldReturnFailure_WhenCasesCompleteWithoutValue()
    {
        var channel = MakeChannel<int>();
        channel.Writer.TryComplete();

    var result = await SelectAsync(cancellationToken: TestContext.Current.CancellationToken, cases: [ChannelCase.Create(channel.Reader, (_, _) => Task.FromResult(Result.Ok(Go.Unit.Value)))]);

    Assert.True(result.IsFailure);
    Assert.Equal(ErrorCodes.SelectDrained, result.Error?.Code);
    }

    [Fact]
    public async Task SelectAsync_ShouldWrapContinuationExceptions()
    {
        var channel = MakeChannel<int>();
        channel.Writer.TryWrite(42);
        channel.Writer.TryComplete();

    var result = await SelectAsync(cancellationToken: TestContext.Current.CancellationToken, cases: [ChannelCase.Create(channel.Reader, (Action<int>)(_ => throw new InvalidOperationException("boom")))]);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Contains("boom", result.Error?.Message);
    }

    [Fact]
    public async Task SelectAsync_ShouldReturnFirstCompletedCase()
    {
        var channel1 = MakeChannel<int>();
        var channel2 = MakeChannel<int>();
        var firstExecuted = false;
        var secondExecuted = false;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var selectTask = SelectAsync(
            cancellationToken: cts.Token,
            cases:
            [
                ChannelCase.Create(channel1.Reader, (value, _) =>
                {
                    firstExecuted = true;
                    Assert.Equal(42, value);
                    return Task.FromResult(Result.Ok(Go.Unit.Value));
                }),
                ChannelCase.Create(channel2.Reader, (value, _) =>
                {
                    secondExecuted = true;
                    return Task.FromResult(Result.Ok(Go.Unit.Value));
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

    [Fact]
    public async Task SelectAsync_ShouldReturnTimeoutError_WhenDeadlineExpires()
    {
        var channel = MakeChannel<int>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var provider = new FakeTimeProvider();

        var selectTask = SelectAsync(
            timeout: TimeSpan.FromSeconds(1),
            provider: provider,
            cancellationToken: cts.Token,
            cases:
            [
                ChannelCase.Create(channel.Reader, (_, _) => Task.FromResult(Result.Ok(Go.Unit.Value)))
            ]);

        Assert.False(selectTask.IsCompleted);

        provider.Advance(TimeSpan.FromSeconds(1));

        var result = await selectTask;

        channel.Writer.TryComplete();

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Timeout, result.Error?.Code);
    }

    [Fact]
    public async Task SelectAsync_ShouldRespectCancellation()
    {
        var channel = MakeChannel<int>();
        using var cts = new CancellationTokenSource();

        var selectTask = SelectAsync(
            cancellationToken: cts.Token,
            cases: [ChannelCase.Create(channel.Reader, (_, _) => Task.FromResult(Result.Ok(Go.Unit.Value)))]);

        cts.CancelAfter(20);

        var result = await selectTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        Assert.True(result.Error!.TryGetMetadata("cancellationToken", out CancellationToken recordedToken));
        Assert.Equal(cts.Token, recordedToken);
        channel.Writer.TryComplete();
    }

    [Fact]
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

            var result = await SelectAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                cases:
                [
                    ChannelCase.Create(channel.Reader, (_, _) => Task.FromResult(Result.Ok(Unit.Value)))
                ]);

            Assert.True(result.IsSuccess);

            var activity = Assert.Single(stoppedActivities);
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

    [Fact]
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

            var result = await SelectAsync(
                cancellationToken: TestContext.Current.CancellationToken,
                cases:
                [
                    ChannelCase.Create(channel.Reader, (_, _) => Task.FromResult(Result.Fail<Unit>(Error.From("failure", "error.activity"))))
                ]);

            Assert.True(result.IsFailure);
            Assert.Equal("error.activity", result.Error?.Code);

            var activity = Assert.Single(stoppedActivities);
            Assert.Equal(ActivityStatusCode.Error, activity.Status);
            Assert.Equal("error", activity.GetTagItem("hugo.select.outcome"));
            Assert.Equal("error.activity", activity.GetTagItem("hugo.error.code"));
        }
        finally
        {
            GoDiagnostics.Reset();
        }
    }

    [Fact]
    public async Task SelectFanInAsync_ShouldDrainAllCases()
    {
        var channel1 = MakeChannel<int>();
        var channel2 = MakeChannel<int>();
        var observed = new ConcurrentBag<int>();

        var fanInTask = SelectFanInAsync(
            [channel1.Reader, channel2.Reader],
            (int value, CancellationToken _) =>
            {
                observed.Add(value);
                return Task.FromResult(Result.Ok(Unit.Value));
            },
            cancellationToken: TestContext.Current.CancellationToken);

        var ct = TestContext.Current.CancellationToken;
        var producers = Task.WhenAll(
            Task.Run(async () =>
            {
                await channel1.Writer.WriteAsync(11, ct);
                channel1.Writer.TryComplete();
            }, ct),
            Task.Run(async () =>
            {
                await channel2.Writer.WriteAsync(22, ct);
                channel2.Writer.TryComplete();
            }, ct));

        await producers;
        var result = await fanInTask;

        Assert.True(result.IsSuccess);
        Assert.Contains(11, observed);
        Assert.Contains(22, observed);
        Assert.Equal(2, observed.Count);
    }

    [Fact]
    public async Task SelectFanInAsync_ShouldPropagateCaseFailure()
    {
        var channel = MakeChannel<int>();
        var fanInTask = SelectFanInAsync(
            [channel.Reader],
            (int _, CancellationToken _) => Task.FromResult(Result.Fail<Unit>(Error.From("boom", ErrorCodes.Validation))),
            cancellationToken: TestContext.Current.CancellationToken);

        await channel.Writer.WriteAsync(5, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var result = await fanInTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
        Assert.Equal("boom", result.Error?.Message);
    }

    [Fact]
    public async Task SelectFanInAsync_ShouldRespectCancellation()
    {
        var channel = MakeChannel<int>();
        using var cts = new CancellationTokenSource(25);

        var result = await SelectFanInAsync(
            [channel.Reader],
            (int _, CancellationToken _) => Task.FromResult(Result.Ok(Unit.Value)),
            cancellationToken: cts.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
        channel.Writer.TryComplete();
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
                return Task.FromResult(Result.Ok(Unit.Value));
            })
        };

        await channel.Writer.WriteAsync(1, TestContext.Current.CancellationToken);
        var first = await SelectAsync(cancellationToken: TestContext.Current.CancellationToken, cases: cases);
        Assert.True(first.IsSuccess);

        await channel.Writer.WriteAsync(2, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var second = await SelectAsync(cancellationToken: TestContext.Current.CancellationToken, cases: cases);

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

        var cases = templates.With((value, _) =>
        {
            observed.Add(value);
            return Task.FromResult(Result.Ok(Unit.Value));
        });

        var selectTask = SelectAsync(cancellationToken: TestContext.Current.CancellationToken, cases: cases);

        await channel2.Writer.WriteAsync(99, TestContext.Current.CancellationToken);
        channel1.Writer.TryComplete();
        channel2.Writer.TryComplete();

        var result = await selectTask;

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, observed);
    }

    [Fact]
    public async Task SelectBuilder_ShouldReturnProjectedResult_WhenTemplateUsed()
    {
        var channel = MakeChannel<int>();
        var template = ChannelCaseTemplates.From(channel.Reader);

        var selectTask = Select<string>(cancellationToken: TestContext.Current.CancellationToken)
            .Case(template, value => $"value:{value}")
            .ExecuteAsync();

        await channel.Writer.WriteAsync(7, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var result = await selectTask;

        Assert.True(result.IsSuccess);
        Assert.Equal("value:7", result.Value);
    }

    [Fact]
    public async Task SelectBuilder_ShouldPropagateCaseFailure()
    {
        var channel = MakeChannel<int>();

        var selectTask = Select<int>(cancellationToken: TestContext.Current.CancellationToken)
            .Case(channel.Reader, (_, _) => Task.FromResult(Result.Fail<int>(Error.From("boom", ErrorCodes.Validation))))
            .ExecuteAsync();

        await channel.Writer.WriteAsync(5, TestContext.Current.CancellationToken);
        channel.Writer.TryComplete();

        var result = await selectTask;

        Assert.True(result.IsFailure);
        Assert.Equal("boom", result.Error?.Message);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact]
    public async Task SelectBuilder_ShouldPropagateTimeout()
    {
        var provider = new FakeTimeProvider();
        var channel = MakeChannel<int>();

        var selectTask = Select<string>(TimeSpan.FromSeconds(1), provider, TestContext.Current.CancellationToken)
            .Case(channel.Reader, value => value.ToString())
            .ExecuteAsync();

        provider.Advance(TimeSpan.FromSeconds(1));

        var result = await selectTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Timeout, result.Error?.Code);
    }

    [Fact]
    public async Task SelectBuilder_ShouldThrow_WhenNoCasesConfigured()
    {
        var builder = Select<int>(cancellationToken: TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.ExecuteAsync());
    }

    [Theory]
    [InlineData(new[] { 1, 2 })]
    public async Task FanInAsync_ShouldMergeIntoDestination(int[] expected)
    {
        var source1 = MakeChannel<int>();
        var source2 = MakeChannel<int>();
        var destination = MakeChannel<int>();

        var ct = TestContext.Current.CancellationToken;
        var fanInTask = FanInAsync([source1.Reader, source2.Reader], destination.Writer, cancellationToken: ct);

        var producers = Task.WhenAll(
            Task.Run(async () =>
            {
                await source1.Writer.WriteAsync(1, ct);
                source1.Writer.TryComplete();
            }, ct),
            Task.Run(async () =>
            {
                await source2.Writer.WriteAsync(2, ct);
                source2.Writer.TryComplete();
            }, ct));

        await producers;
        var result = await fanInTask;

        Assert.True(result.IsSuccess);

    var observed = new List<int>();
    await foreach (var value in destination.Reader.ReadAllAsync(ct))
        {
            observed.Add(value);
        }

        observed.Sort();
        Assert.Equal(expected, observed);
    }

    [Fact]
    public async Task FanInAsync_ShouldReturnFailure_WhenDestinationClosed()
    {
        var source = MakeChannel<int>();
        var destination = MakeChannel<int>();
        destination.Writer.TryComplete(new InvalidOperationException("closed"));

        var fanInTask = FanInAsync([source.Reader], destination.Writer, cancellationToken: TestContext.Current.CancellationToken);

        await source.Writer.WriteAsync(42, TestContext.Current.CancellationToken);
        source.Writer.TryComplete();

        var result = await fanInTask;

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await destination.Reader.Completion);
    }

    [Theory]
    [InlineData(new[] { 7, 9 })]
    public async Task FanIn_ShouldMergeSources(int[] expected)
    {
        var source1 = MakeChannel<int>();
        var source2 = MakeChannel<int>();

        var ct = TestContext.Current.CancellationToken;
        var merged = FanIn([source1.Reader, source2.Reader], cancellationToken: ct);

        var producers = Task.WhenAll(
            Task.Run(async () =>
            {
                await source1.Writer.WriteAsync(7, ct);
                source1.Writer.TryComplete();
            }, ct),
            Task.Run(async () =>
            {
                await source2.Writer.WriteAsync(9, ct);
                source2.Writer.TryComplete();
            }, ct));

        await producers;

    var values = new List<int>();
    await foreach (var value in merged.ReadAllAsync(ct))
        {
            values.Add(value);
        }

        values.Sort();
        Assert.Equal(expected, values);
    }

    [Fact]
    public async Task FanIn_ShouldPropagateCancellation()
    {
        var source = MakeChannel<int>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var merged = FanIn([source.Reader], cancellationToken: cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await merged.ReadAsync(cts.Token));
        source.Writer.TryComplete();
    }

    [Fact]
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

    [Fact]
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

    [Fact]
    public void MakeChannel_WithZeroCapacity_ShouldCreateUnboundedChannel()
    {
        var channel = MakeChannel<int>(0);

        Assert.True(channel.Writer.TryWrite(1));
        Assert.True(channel.Writer.TryWrite(2));
    }

    [Fact]
    public void MakeChannel_WithNullBoundedOptions_ShouldThrow() => Assert.Throws<ArgumentNullException>(() => MakeChannel<int>((BoundedChannelOptions)null!));

    [Fact]
    public void MakeChannel_WithNullUnboundedOptions_ShouldThrow() => Assert.Throws<ArgumentNullException>(() => MakeChannel<int>((UnboundedChannelOptions)null!));

    [Fact]
    public void MakeChannel_WithNullPrioritizedOptions_ShouldThrow() => Assert.Throws<ArgumentNullException>(() => MakeChannel<int>((PrioritizedChannelOptions)null!));

    [Fact]
    public void MakePrioritizedChannel_ShouldThrow_WhenPriorityLevelsInvalid() => Assert.Throws<ArgumentOutOfRangeException>(() => MakePrioritizedChannel<int>(0));

    [Fact]
    public void MakePrioritizedChannel_ShouldThrow_WhenDefaultPriorityTooHigh() => Assert.Throws<ArgumentOutOfRangeException>(() => MakePrioritizedChannel<int>(priorityLevels: 2, defaultPriority: 2));

    private static readonly int[] expected = [2, 3, 1];

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task PrioritizedChannelWriter_ShouldRejectInvalidPriority()
    {
        var channel = MakeChannel<int>(new PrioritizedChannelOptions { PriorityLevels = 2 });
        var writer = channel.PrioritizedWriter;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(async () => await writer.WriteAsync(42, priority: 5, TestContext.Current.CancellationToken));
    }

    [Fact]
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
            ActivityStopped = activity => stoppedActivities.Add(activity)
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
