// Import the Hugo helpers to use them without the 'Hugo.' prefix.
using static Hugo.Go;

namespace Hugo.Tests;

public class GoSharpTests
{
    // This helper is not a test, so it's okay for it to write to the console.
    private static (string Content, Error? Err) ReadFileContent(string path)
    {
        using (Defer(() => Console.WriteLine("[ReadFileContent] Cleanup finished.")))
        {
            if (string.IsNullOrWhiteSpace(path))
                return Err<string>("Path cannot be null or empty.");
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
        using (await mutex.LockAsync(TestContext.Current.CancellationToken))
        {
            var waitingTask = mutex.LockAsync(cts.Token);
            await Task.Delay(50, TestContext.Current.CancellationToken);
            cts.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => waitingTask);
        }
    }

    [Fact]
    public async Task Mutex_ShouldNotBeReentrant()
    {
        var mutex = new Mutex();
        using (await mutex.LockAsync(TestContext.Current.CancellationToken))
        {
            var reentrantLockTask = mutex.LockAsync(TestContext.Current.CancellationToken);
            var completedTask = await Task.WhenAny(
                reentrantLockTask,
                Task.Delay(100, TestContext.Current.CancellationToken)
            );
            Assert.NotEqual(reentrantLockTask, completedTask);
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
            wg.Go(() =>
            {
                once.Do(() => Interlocked.Increment(ref counter));
                return Task.CompletedTask;
            });
        }
        await wg.WaitAsync();
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
        var maxReaders = 0;
        var readersInside = 0;
        var wg = new WaitGroup();
        for (var i = 0; i < 5; i++)
        {
            wg.Go(async () =>
            {
                using (await rwMutex.RLockAsync())
                {
                    var currentReaders = Interlocked.Increment(ref readersInside);
                    maxReaders = Math.Max(maxReaders, currentReaders);
                    await Task.Delay(50);
                    Interlocked.Decrement(ref readersInside);
                }
            });
        }
        await wg.WaitAsync();
        Assert.Equal(5, maxReaders);
    }

    [Fact]
    public async Task RWMutex_ShouldProvide_ExclusiveWriteLock()
    {
        var rwMutex = new RwMutex();
        var writerFinished = false;
        Task readerTask,
            writerTask;

        using (await rwMutex.LockAsync(TestContext.Current.CancellationToken)) // Acquire the main write lock.
        {
            // Start a reader and a writer task that will wait.
            readerTask = Task.Run(
                async () =>
                {
                    using (await rwMutex.RLockAsync())
                    {
                        Assert.True(writerFinished);
                    }
                },
                TestContext.Current.CancellationToken
            );
            writerTask = Task.Run(
                async () =>
                {
                    using (await rwMutex.LockAsync())
                    {
                        Assert.True(writerFinished);
                    }
                },
                TestContext.Current.CancellationToken
            );

            await Task.Delay(100, TestContext.Current.CancellationToken); // Give tasks time to block.
            writerFinished = true;
        } // The write lock is released here.

        // Both tasks should now be able to complete.
        await Task.WhenAll(readerTask, writerTask);
    }

    [Fact]
    public void ReadFileContent_ShouldSucceed_WithValidFile()
    {
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, "Hello from GoSharp!");
        var (content, err) = ReadFileContent(tempFile);
        Assert.Null(err);
        Assert.Equal("Hello from GoSharp!", content);
        File.Delete(tempFile);
    }

    [Fact]
    public void ReadFileContent_ShouldFail_WithInvalidFile()
    {
        var (content, err) = ReadFileContent("non_existent_file.txt");
        Assert.NotNull(err);
        Assert.Null(content);
        Assert.Contains("Could not find file", err.Message);
    }

    [Fact]
    public async Task Concurrency_ShouldCommunicate_ViaChannel()
    {
        var channel = MakeChannel<string>();
        Run(async () =>
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
            wg.Go(async () =>
            {
                await Task.Delay(20);
                Interlocked.Increment(ref counter);
            });
        }
        await wg.WaitAsync();
        Assert.Equal(5, counter);
    }

    [Fact]
    public async Task WaitGroup_ShouldCompleteImmediately_WhenNoTasksAreAdded()
    {
        var wg = new WaitGroup();
        await wg.WaitAsync();
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
            wg.Go(async () =>
            {
                for (var j = 0; j < incrementsPerTask; j++)
                {
                    using (await mutex.LockAsync())
                    {
                        var currentValue = counter;
                        await Task.Delay(1);
                        counter = currentValue + 1;
                    }
                }
            });
        }
        await wg.WaitAsync();
        Assert.Equal(numTasks * incrementsPerTask, counter);
    }
}
