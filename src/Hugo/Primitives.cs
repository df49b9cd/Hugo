using System.Collections.Concurrent;

namespace Hugo;

/// <summary>
/// A WaitGroup waits for a collection of tasks to finish, similar to Hugo's `sync.WaitGroup`.
/// </summary>
public class WaitGroup
{
    private readonly List<Task> _tasks = [];

    public void Add(Task task)
    {
        lock (_tasks)
        {
            _tasks.Add(task);
        }
    }

    public Task WaitAsync()
    {
        lock (_tasks)
        {
            return Task.WhenAll(_tasks.ToArray());
        }
    }
}

/// <summary>
/// A Mutex is a mutual exclusion lock, similar to Hugo's `sync.Mutex`.
/// It is non-reentrant.
/// </summary>
public class Mutex
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task<IDisposable> LockAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken);
        return new Releaser(_semaphore);
    }

    private readonly struct Releaser(SemaphoreSlim semaphoreSlim) : IDisposable
    {
        public void Dispose() => semaphoreSlim.Release();
    }
}

/// <summary>
/// A RWMutex is a reader/writer mutual exclusion lock, similar to Hugo's `sync.RWMutex`.
/// It allows for concurrent reads or a single, exclusive write.
/// </summary>
public class RwMutex
{
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private int _readerCount;

    public async Task<IDisposable> RLockAsync(CancellationToken cancellationToken = default)
    {
        await _readerGate.WaitAsync(cancellationToken);
        if (Interlocked.Increment(ref _readerCount) == 1)
        {
            await _writerGate.WaitAsync(cancellationToken);
        }
        _readerGate.Release();
        return new ReadReleaser(this);
    }

    public async Task<IDisposable> LockAsync(CancellationToken cancellationToken = default)
    {
        await _writerGate.WaitAsync(cancellationToken);
        return new WriteReleaser(this);
    }

    private void RUnlock()
    {
        if (Interlocked.Decrement(ref _readerCount) == 0)
        {
            _writerGate.Release();
        }
    }

    private void Unlock() => _writerGate.Release();

    private readonly struct ReadReleaser(RwMutex rwMutex) : IDisposable
    {
        public void Dispose() => rwMutex.RUnlock();
    }

    private readonly struct WriteReleaser(RwMutex rwMutex) : IDisposable
    {
        public void Dispose() => rwMutex.Unlock();
    }
}

/// <summary>
/// A Once is an object that will perform an action exactly once.
/// </summary>
public class Once
{
    private int _done;
    private readonly object _lock = new();

    public void Do(Action action)
    {
        if (Volatile.Read(ref _done) == 1)
            return;
        lock (_lock)
        {
            if (_done == 0)
            {
                try
                {
                    action();
                }
                finally
                {
                    Volatile.Write(ref _done, 1);
                }
            }
        }
    }
}

/// <summary>
/// A Pool is a concurrency-safe pool of reusable objects, similar to Hugo's `sync.Pool`.
/// </summary>
public class Pool<T>
    where T : class
{
    private readonly ConcurrentBag<T> _items = [];
    public Func<T>? New { get; init; }

    public T Get()
    {
        if (_items.TryTake(out var item))
            return item;
        return New?.Invoke()
            ?? throw new InvalidOperationException(
                "The 'New' factory function must be set before Get can be called on an empty pool."
            );
    }

    public void Put(T item) => _items.Add(item);
}

/// <summary>
/// Represents a simple, Hugo-style error.
/// </summary>
public class Error(string message)
{
    public string Message { get; } = message ?? throw new ArgumentNullException(nameof(message));

    public override string ToString() => Message;

    public static implicit operator Error?(string? message) =>
        message == null ? null : new Error(message);

    public static implicit operator Error?(Exception? ex) =>
        ex == null ? null : new Error(ex.Message);
}

/// <summary>
/// A helper struct that enables the `defer` functionality via the IDisposable pattern.
/// </summary>
public readonly struct Defer(Action action) : IDisposable
{
    public void Dispose() => action.Invoke();
}
