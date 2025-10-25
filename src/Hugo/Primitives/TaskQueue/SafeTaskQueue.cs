using System.Diagnostics.CodeAnalysis;
using Unit = Hugo.Go.Unit;

namespace Hugo;

/// <summary>
/// Provides a result-based wrapper around <see cref="TaskQueue{T}"/> that converts common exceptions into <see cref="Result{T}"/> failures.
/// </summary>
/// <typeparam name="T">Work item type.</typeparam>
public sealed class SafeTaskQueue<T> : IAsyncDisposable
{
    private readonly TaskQueue<T> _queue;
    private readonly bool _ownsQueue;

    /// <summary>
    /// Initializes a new instance of the <see cref="SafeTaskQueue{T}"/> class.
    /// </summary>
    /// <param name="queue">Underlying task queue to wrap.</param>
    /// <param name="ownsQueue">True to dispose the underlying queue when this wrapper is disposed.</param>
    public SafeTaskQueue(TaskQueue<T> queue, bool ownsQueue = false)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _ownsQueue = ownsQueue;
    }

    /// <summary>
    /// Gets the underlying queue for scenarios that require direct access.
    /// </summary>
    public TaskQueue<T> UnsafeQueue => _queue;

    /// <summary>
    /// Enqueues the supplied work item, returning a failure result instead of throwing when the operation cannot be completed.
    /// </summary>
    public async ValueTask<Result<Unit>> EnqueueAsync(T value, CancellationToken cancellationToken = default)
    {
        try
        {
            await _queue.EnqueueAsync(value, cancellationToken).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<Unit>(Error.Canceled(token: oce.CancellationToken));
        }
        catch (ObjectDisposedException)
        {
            return Result.Fail<Unit>(QueueDisposed());
        }
        catch (Exception ex)
        {
            return Result.Fail<Unit>(Error.FromException(ex));
        }
    }

    /// <summary>
    /// Leases the next available work item.
    /// </summary>
    public async ValueTask<Result<SafeTaskQueueLease<T>>> LeaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            TaskQueueLease<T> lease = await _queue.LeaseAsync(cancellationToken).ConfigureAwait(false);
            return Result.Ok(SafeTaskQueueLease<T>.From(lease));
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<SafeTaskQueueLease<T>>(Error.Canceled(token: oce.CancellationToken));
        }
        catch (ObjectDisposedException)
        {
            return Result.Fail<SafeTaskQueueLease<T>>(QueueDisposed());
        }
        catch (Exception ex)
        {
            return Result.Fail<SafeTaskQueueLease<T>>(Error.FromException(ex));
        }
    }

    /// <summary>
    /// Creates a safe wrapper around an existing lease instance.
    /// </summary>
    public SafeTaskQueueLease<T> Wrap(TaskQueueLease<T> lease) => SafeTaskQueueLease<T>.From(lease);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ownsQueue)
        {
            await _queue.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Error QueueDisposed() => Error.From("The task queue has been disposed.", ErrorCodes.TaskQueueDisposed);
}

/// <summary>
/// Provides result-based helpers for managing a leased work item.
/// </summary>
/// <typeparam name="T">Work item type.</typeparam>
public sealed class SafeTaskQueueLease<T>
{
    private readonly TaskQueueLease<T> _lease;

    private SafeTaskQueueLease(TaskQueueLease<T> lease)
    {
        _lease = lease ?? throw new ArgumentNullException(nameof(lease));
    }

    /// <summary>
    /// Wraps the specified lease instance.
    /// </summary>
    public static SafeTaskQueueLease<T> From(TaskQueueLease<T> lease) => new(lease);

    /// <summary>
    /// Gets the leased value.
    /// </summary>
    public T Value => _lease.Value;

    /// <summary>
    /// Gets the attempt number associated with the lease.
    /// </summary>
    public int Attempt => _lease.Attempt;

    /// <summary>
    /// Gets the enqueue timestamp for the leased value.
    /// </summary>
    public DateTimeOffset EnqueuedAt => _lease.EnqueuedAt;

    /// <summary>
    /// Gets the last error that caused the value to be re-queued.
    /// </summary>
    public Error? LastError => _lease.LastError;

    /// <summary>
    /// Completes the lease.
    /// </summary>
    public ValueTask<Result<Unit>> CompleteAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _lease.CompleteAsync(cancellationToken));

    /// <summary>
    /// Sends a heartbeat to extend the lease duration.
    /// </summary>
    public ValueTask<Result<Unit>> HeartbeatAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(() => _lease.HeartbeatAsync(cancellationToken));

    /// <summary>
    /// Fails the lease and optionally re-queues the work item.
    /// </summary>
    public ValueTask<Result<Unit>> FailAsync(Error? error, bool requeue = true, CancellationToken cancellationToken = default)
    {
        if (error is null)
        {
            return ValueTask.FromResult(Result.Fail<Unit>(Error.From("Error must be provided when failing a lease.", ErrorCodes.Validation)));
        }

        return ExecuteAsync(() => _lease.FailAsync(error, requeue, cancellationToken));
    }

    private static async ValueTask<Result<Unit>> ExecuteAsync(Func<ValueTask> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<Unit>(Error.Canceled(token: oce.CancellationToken));
        }
        catch (ObjectDisposedException)
        {
            return Result.Fail<Unit>(Error.From("The task queue has been disposed.", ErrorCodes.TaskQueueDisposed));
        }
        catch (InvalidOperationException)
        {
            return Result.Fail<Unit>(Error.From("Lease is no longer active.", ErrorCodes.TaskQueueLeaseInactive));
        }
        catch (Exception ex)
        {
            return Result.Fail<Unit>(Error.FromException(ex));
        }
    }
}
