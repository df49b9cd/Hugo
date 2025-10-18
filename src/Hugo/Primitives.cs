using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace Hugo;

/// <summary>
/// Coordinates the completion of a set of asynchronous operations, similar to Go's <c>sync.WaitGroup</c>.
/// </summary>
public sealed class WaitGroup
{
    private static TaskCompletionSource<bool> CompletedSource
    {
        get
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            tcs.TrySetResult(true);
            return tcs;
        }
    }

    private volatile TaskCompletionSource<bool> _tcs = CompletedSource;
    private int _count;

    /// <summary>
    /// Gets the number of outstanding operations tracked by the wait group.
    /// </summary>
    public int Count => Math.Max(Volatile.Read(ref _count), 0);

    /// <summary>
    /// Adds a positive delta to the wait counter.
    /// </summary>
    public void Add(int delta)
    {
        if (delta <= 0)
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "The delta must be a positive value.");

        var newValue = Interlocked.Add(ref _count, delta);
        if (newValue <= 0)
        {
            Interlocked.Exchange(ref _count, 0);
            throw new InvalidOperationException("WaitGroup counter cannot become negative.");
        }

        if (newValue == delta)
        {
            var newSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Interlocked.Exchange(ref _tcs, newSource);
        }
    }

    /// <summary>
    /// Tracks the provided task and marks the wait group complete when it finishes.
    /// </summary>
    public void Add(Task task)
    {
        if (task is null)
            throw new ArgumentNullException(nameof(task));

        Add(1);
        task.ContinueWith(static (t, state) => ((WaitGroup)state!).Done(), this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Runs the supplied delegate via <see cref="Task.Run(Action)"/> and tracks it.
    /// </summary>
    public void Go(Func<Task> work, CancellationToken cancellationToken = default)
    {
        if (work is null)
            throw new ArgumentNullException(nameof(work));

        Add(1);

        Task.Run(async () =>
        {
            try
            {
                await work().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            finally
            {
                Done();
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Signals that one of the registered operations has completed.
    /// </summary>
    public void Done()
    {
        var newValue = Interlocked.Decrement(ref _count);
        if (newValue < 0)
        {
            Interlocked.Exchange(ref _count, 0);
            throw new InvalidOperationException("WaitGroup counter cannot become negative.");
        }

        if (newValue == 0)
        {
            _tcs.TrySetResult(true);
        }
    }

    /// <summary>
    /// Asynchronously waits for all registered operations to complete.
    /// </summary>
    public Task WaitAsync(CancellationToken cancellationToken = default)
    {
        if (Count == 0)
            return Task.CompletedTask;

        var awaitable = _tcs.Task;
        return cancellationToken.CanBeCanceled ? awaitable.WaitAsync(cancellationToken) : awaitable;
    }
}

/// <summary>
/// Provides both synchronous and asynchronous mutual exclusion primitives.
/// </summary>
public sealed class Mutex
{
    private readonly SemaphoreSlim _asyncLock = new(1, 1);
    private readonly Lock _lock = new();

    /// <summary>
    /// Enters the synchronous critical section, returning a scope that releases the lock when disposed.
    /// </summary>
    public Lock.Scope EnterScope() => _lock.EnterScope();

    /// <summary>
    /// Asynchronously waits for the mutex and returns a releaser that unlocks when disposed.
    /// </summary>
    public async ValueTask<AsyncLockReleaser> LockAsync(CancellationToken cancellationToken = default)
    {
        await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new AsyncLockReleaser(_asyncLock);
    }

    /// <summary>
    /// Releases the asynchronous lock when disposed.
    /// </summary>
    public readonly struct AsyncLockReleaser : IAsyncDisposable, IDisposable
    {
        private readonly SemaphoreSlim _semaphore;

        public AsyncLockReleaser(SemaphoreSlim semaphore) => _semaphore = semaphore ?? throw new ArgumentNullException(nameof(semaphore));

        public void Dispose() => _semaphore.Release();

        public ValueTask DisposeAsync()
        {
            _semaphore.Release();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Provides reader/writer mutual exclusion semantics with both synchronous and asynchronous APIs.
/// </summary>
public sealed class RwMutex
{
    private readonly ReaderWriterLockSlim _sync = new(LockRecursionPolicy.NoRecursion);
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private int _readerCount;

    public IDisposable EnterReadScope()
    {
        _sync.EnterReadLock();
        return new ReadScope(_sync);
    }

    public IDisposable EnterWriteScope()
    {
        _sync.EnterWriteLock();
        return new WriteScope(_sync);
    }

    public async ValueTask<AsyncReadReleaser> RLockAsync(CancellationToken cancellationToken = default)
    {
        await _readerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var incremented = false;
        try
        {
            if (Interlocked.Increment(ref _readerCount) == 1)
            {
                incremented = true;
                await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                incremented = true;
            }
        }
        catch
        {
            if (incremented && Interlocked.Decrement(ref _readerCount) == 0)
            {
                _writerGate.Release();
            }

            _readerGate.Release();
            throw;
        }

        _readerGate.Release();
        return new AsyncReadReleaser(this);
    }

    public async ValueTask<AsyncWriteReleaser> LockAsync(CancellationToken cancellationToken = default)
    {
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new AsyncWriteReleaser(this);
    }

    private void ReleaseReader()
    {
        if (Interlocked.Decrement(ref _readerCount) == 0)
        {
            _writerGate.Release();
        }
    }

    private void ReleaseWriter() => _writerGate.Release();

    private readonly struct ReadScope : IDisposable
    {
        private readonly ReaderWriterLockSlim _rwLock;

        public ReadScope(ReaderWriterLockSlim rwLock) => _rwLock = rwLock;

        public void Dispose() => _rwLock.ExitReadLock();
    }

    private readonly struct WriteScope : IDisposable
    {
        private readonly ReaderWriterLockSlim _rwLock;

        public WriteScope(ReaderWriterLockSlim rwLock) => _rwLock = rwLock;

        public void Dispose() => _rwLock.ExitWriteLock();
    }

    public readonly struct AsyncReadReleaser : IAsyncDisposable, IDisposable
    {
        private readonly RwMutex _mutex;

        public AsyncReadReleaser(RwMutex mutex) => _mutex = mutex;

        public void Dispose() => _mutex.ReleaseReader();

        public ValueTask DisposeAsync()
        {
            _mutex.ReleaseReader();
            return ValueTask.CompletedTask;
        }
    }

    public readonly struct AsyncWriteReleaser : IAsyncDisposable, IDisposable
    {
        private readonly RwMutex _mutex;

        public AsyncWriteReleaser(RwMutex mutex) => _mutex = mutex;

        public void Dispose() => _mutex.ReleaseWriter();

        public ValueTask DisposeAsync()
        {
            _mutex.ReleaseWriter();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// A Once is an object that will perform an action exactly once.
/// </summary>
public sealed class Once
{
    private int _done;
    private readonly object _lock = new();

    public void Do(Action action)
    {
        if (action is null)
            throw new ArgumentNullException(nameof(action));

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
/// A concurrency-safe pool of reusable objects, similar to Go's <c>sync.Pool</c>.
/// </summary>
public sealed class Pool<T>
    where T : class
{
    private readonly ConcurrentBag<T> _items = [];

    public Func<T>? New { get; init; }

    public T Get()
    {
        if (_items.TryTake(out var item))
            return item;

        return New?.Invoke() ?? throw new InvalidOperationException("The 'New' factory function must be set before Get can be called on an empty pool.");
    }

    public void Put(T item)
    {
        if (item is null)
            throw new ArgumentNullException(nameof(item));

        _items.Add(item);
    }
}

/// <summary>
/// Represents a structured error with optional metadata, code, and cause.
/// </summary>
public sealed class Error
{
    private static readonly IReadOnlyDictionary<string, object?> EmptyMetadata = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    private Error(string message, string? code, Exception? cause, IReadOnlyDictionary<string, object?> metadata)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Code = code;
        Cause = cause;
        Metadata = metadata ?? EmptyMetadata;
    }

    public string Message { get; }

    public string? Code { get; }

    public Exception? Cause { get; }

    public IReadOnlyDictionary<string, object?> Metadata { get; }

    public Error WithMetadata(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Metadata key must be provided.", nameof(key));

        var metadata = new Dictionary<string, object?>(Metadata, StringComparer.OrdinalIgnoreCase)
        {
            [key] = value
        };

        return new Error(Message, Code, Cause, new ReadOnlyDictionary<string, object?>(metadata));
    }

    public Error WithMetadata(IEnumerable<KeyValuePair<string, object?>> metadata)
    {
        if (metadata is null)
            throw new ArgumentNullException(nameof(metadata));

        var merged = new Dictionary<string, object?>(Metadata, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in metadata)
        {
            merged[kvp.Key] = kvp.Value;
        }

        return new Error(Message, Code, Cause, new ReadOnlyDictionary<string, object?>(merged));
    }

    public Error WithCode(string? code) => new(Message, code, Cause, Metadata);

    public Error WithCause(Exception? cause) => new(Message, Code, cause, Metadata);

    public bool TryGetMetadata<T>(string key, out T? value)
    {
        if (Metadata.TryGetValue(key, out var obj) && obj is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public override string ToString() => Code is null ? Message : $"{Code}: {Message}";

    public static Error From(string message, string? code = null, Exception? cause = null, IReadOnlyDictionary<string, object?>? metadata = null) =>
        new(message, code, cause, metadata ?? EmptyMetadata);

    public static Error FromException(Exception exception, string? code = null)
    {
        if (exception is null)
            throw new ArgumentNullException(nameof(exception));

        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["exceptionType"] = exception.GetType().FullName,
            ["stackTrace"] = exception.StackTrace
        };

        return new Error(exception.Message, code ?? ErrorCodes.Exception, exception, new ReadOnlyDictionary<string, object?>(metadata));
    }

    public static Error Canceled(string? message = null, CancellationToken? token = null)
    {
        var metadata = token is { } t
            ? new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["cancellationToken"] = t })
            : EmptyMetadata;

        return new Error(message ?? "Operation was canceled.", ErrorCodes.Canceled, token is { } tk ? new OperationCanceledException(tk) : null, metadata);
    }

    public static Error Timeout(TimeSpan? duration = null, string? message = null)
    {
        var metadata = duration.HasValue
            ? new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["duration"] = duration.Value })
            : EmptyMetadata;

        return new Error(message ?? "The operation timed out.", ErrorCodes.Timeout, null, metadata);
    }

    public static Error Unspecified(string? message = null) => new(message ?? "An unspecified error occurred.", ErrorCodes.Unspecified, null, EmptyMetadata);

    public static Error Aggregate(string message, params Error[] errors)
    {
        if (errors is null)
            throw new ArgumentNullException(nameof(errors));

        var metadata = new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["errors"] = errors
        });

        return new Error(message, ErrorCodes.Aggregate, null, metadata);
    }

    public static implicit operator Error?(string? message) => message is null ? null : From(message);

    public static implicit operator Error?(Exception? exception) => exception is null ? null : FromException(exception);
}

/// <summary>
/// A helper struct that enables the <c>defer</c> functionality via the <see cref="IDisposable"/> pattern.
/// </summary>
public readonly struct Defer(Action action) : IDisposable
{
    public void Dispose() => action.Invoke();
}

/// <summary>
/// Well-known error codes emitted by the library.
/// </summary>
public static class ErrorCodes
{
    public const string Unspecified = "error.unspecified";
    public const string Canceled = "error.canceled";
    public const string Timeout = "error.timeout";
    public const string Exception = "error.exception";
    public const string Aggregate = "error.aggregate";
    public const string Validation = "error.validation";
}
