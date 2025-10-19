using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Text.Json.Serialization;

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
        {
            throw new ArgumentOutOfRangeException(nameof(delta), delta, "The delta must be a positive value.");
        }

        var newValue = Interlocked.Add(ref _count, delta);
        if (newValue <= 0)
        {
            Interlocked.Exchange(ref _count, 0);
            throw new InvalidOperationException("WaitGroup counter cannot become negative.");
        }

        GoDiagnostics.RecordWaitGroupAdd(delta);

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
        ArgumentNullException.ThrowIfNull(task);

        Add(1);
        task.ContinueWith(static (t, state) => ((WaitGroup)state!).Done(), this, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Runs the supplied delegate via <see cref="Task.Run(Action)"/> and tracks it.
    /// </summary>
    public void Go(Func<Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);

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

        GoDiagnostics.RecordWaitGroupDone();

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
        {
            return Task.CompletedTask;
        }

        var awaitable = _tcs.Task;
        return cancellationToken.CanBeCanceled ? awaitable.WaitAsync(cancellationToken) : awaitable;
    }

    /// <summary>
    /// Asynchronously waits for all registered operations to complete or for the timeout to elapse.
    /// Returns <c>true</c> when the wait group completed before the timeout expired, otherwise <c>false</c>.
    /// </summary>
    public async Task<bool> WaitAsync(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        if (timeout == Timeout.InfiniteTimeSpan)
        {
            await WaitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        if (timeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        provider ??= TimeProvider.System;

        if (Count == 0)
        {
            return true;
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var waitTask = WaitAsync(linkedCts.Token);
        if (waitTask.IsCompleted)
        {
            linkedCts.Cancel();
            await waitTask.ConfigureAwait(false);
            return true;
        }

        var delayTask = provider.DelayAsync(timeout, linkedCts.Token);
        var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
        if (completed == waitTask)
        {
            linkedCts.Cancel();
            await waitTask.ConfigureAwait(false);
            return true;
        }

        linkedCts.Cancel();
        return false;
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
    public readonly struct AsyncLockReleaser(SemaphoreSlim semaphore) : IAsyncDisposable, IDisposable
    {
        private readonly SemaphoreSlim _semaphore = semaphore ?? throw new ArgumentNullException(nameof(semaphore));

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
        var readerRegistered = false;
        var writerGateAcquired = false;

        try
        {
            var readers = Interlocked.Increment(ref _readerCount);
            readerRegistered = true;

            if (readers == 1)
            {
                await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                writerGateAcquired = true;
            }
        }
        catch
        {
            if (readerRegistered && Interlocked.Decrement(ref _readerCount) == 0 && writerGateAcquired)
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

    private readonly struct ReadScope(ReaderWriterLockSlim rwLock) : IDisposable
    {
        private readonly ReaderWriterLockSlim _rwLock = rwLock;

        public void Dispose() => _rwLock.ExitReadLock();
    }

    private readonly struct WriteScope(ReaderWriterLockSlim rwLock) : IDisposable
    {
        private readonly ReaderWriterLockSlim _rwLock = rwLock;

        public void Dispose() => _rwLock.ExitWriteLock();
    }

    public readonly struct AsyncReadReleaser(RwMutex mutex) : IAsyncDisposable, IDisposable
    {
        private readonly RwMutex _mutex = mutex;

        public void Dispose() => _mutex.ReleaseReader();

        public ValueTask DisposeAsync()
        {
            _mutex.ReleaseReader();
            return ValueTask.CompletedTask;
        }
    }

    public readonly struct AsyncWriteReleaser(RwMutex mutex) : IAsyncDisposable, IDisposable
    {
        private readonly RwMutex _mutex = mutex;

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
        ArgumentNullException.ThrowIfNull(action);

        if (Volatile.Read(ref _done) == 1)
        {
            return;
        }

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

    public T Get() => _items.TryTake(out var item)
            ? item
            : New?.Invoke() ?? throw new InvalidOperationException("The 'New' factory function must be set before Get can be called on an empty pool.");

    public void Put(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        _items.Add(item);
    }
}

/// <summary>
/// Represents a structured error with optional metadata, code, and cause.
/// </summary>
[JsonConverter(typeof(ErrorJsonConverter))]
public sealed class Error
{
    private static readonly StringComparer MetadataComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly FrozenDictionary<string, object?> EmptyMetadata =
        FrozenDictionary.ToFrozenDictionary(Array.Empty<KeyValuePair<string, object?>>(), MetadataComparer);

    private readonly FrozenDictionary<string, object?> _metadata;

    private Error(string message, string? code, Exception? cause, FrozenDictionary<string, object?> metadata)
    {
        Message = message ?? throw new ArgumentNullException(nameof(message));
        Code = code;
        Cause = cause;
        _metadata = metadata ?? EmptyMetadata;
    }

    public string Message { get; }

    public string? Code { get; }

    public Exception? Cause { get; }

    public IReadOnlyDictionary<string, object?> Metadata => _metadata;

    public Error WithMetadata(string key, object? value)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ArgumentException("Metadata key must be provided.", nameof(key));
        }

        var builder = CloneMetadata(_metadata, 1);
        builder[key] = value;

        return new Error(Message, Code, Cause, FreezeMetadata(builder));
    }

    public Error WithMetadata(IEnumerable<KeyValuePair<string, object?>> metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);

        var builder = CloneMetadata(_metadata);
        foreach (var kvp in metadata)
        {
            builder[kvp.Key] = kvp.Value;
        }

        return new Error(Message, Code, Cause, FreezeMetadata(builder));
    }

    public Error WithCode(string? code) => new(Message, code, Cause, _metadata);

    public Error WithCause(Exception? cause) => new(Message, Code, cause, _metadata);

    public bool TryGetMetadata<T>(string key, out T? value)
    {
        if (_metadata.TryGetValue(key, out var obj) && obj is T typed)
        {
            value = typed;
            return true;
        }

        value = default;
        return false;
    }

    public override string ToString() => Code is null ? Message : $"{Code}: {Message}";

    public static Error From(string message, string? code = null, Exception? cause = null, IReadOnlyDictionary<string, object?>? metadata = null) =>
        new(message, code, cause, FreezeMetadata(metadata));

    public static Error FromException(Exception exception, string? code = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var builder = new Dictionary<string, object?>(2, MetadataComparer)
        {
            ["exceptionType"] = exception.GetType().FullName,
            ["stackTrace"] = exception.StackTrace
        };

        return new Error(exception.Message, code ?? ErrorCodes.Exception, exception, FreezeMetadata(builder));
    }

    public static Error Canceled(string? message = null, CancellationToken? token = null)
    {
        var builder = token is { } t
            ? new Dictionary<string, object?>(1, MetadataComparer) { ["cancellationToken"] = t }
            : null;

        return new Error(
            message ?? "Operation was canceled.",
            ErrorCodes.Canceled,
            token is { } tk ? new OperationCanceledException(tk) : null,
            FreezeMetadata(builder));
    }

    public static Error Timeout(TimeSpan? duration = null, string? message = null)
    {
        var builder = duration.HasValue
            ? new Dictionary<string, object?>(1, MetadataComparer) { ["duration"] = duration.Value }
            : null;

        return new Error(message ?? "The operation timed out.", ErrorCodes.Timeout, null, FreezeMetadata(builder));
    }

    public static Error Unspecified(string? message = null) => new(message ?? "An unspecified error occurred.", ErrorCodes.Unspecified, null, EmptyMetadata);

    public static Error Aggregate(string message, params Error[] errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var builder = new Dictionary<string, object?>(1, MetadataComparer)
        {
            ["errors"] = errors
        };

        return new Error(message, ErrorCodes.Aggregate, null, FreezeMetadata(builder));
    }

    public static implicit operator Error?(string? message) => message is null ? null : From(message);

    public static implicit operator Error?(Exception? exception) => exception is null ? null : FromException(exception);

    private static Dictionary<string, object?> CloneMetadata(IReadOnlyDictionary<string, object?> source, int additionalCapacity = 0)
    {
        var builder = new Dictionary<string, object?>(source.Count + additionalCapacity, MetadataComparer);
        foreach (var kvp in source)
        {
            builder[kvp.Key] = kvp.Value;
        }

        return builder;
    }

    private static FrozenDictionary<string, object?> FreezeMetadata(IReadOnlyDictionary<string, object?>? metadata)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return EmptyMetadata;
        }

        return metadata is FrozenDictionary<string, object?> frozen && MetadataComparer.Equals(frozen.Comparer)
            ? frozen
            : FreezeMetadata(CloneMetadata(metadata));
    }

    private static FrozenDictionary<string, object?> FreezeMetadata(Dictionary<string, object?>? builder) => builder is null || builder.Count == 0 ? EmptyMetadata : builder.ToFrozenDictionary(MetadataComparer);
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
    public const string SelectDrained = "error.select.drained";
}
