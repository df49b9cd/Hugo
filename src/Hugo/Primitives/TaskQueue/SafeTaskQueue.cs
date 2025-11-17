using Unit = Hugo.Go.Unit;

namespace Hugo;

/// <summary>
/// Provides a result-based wrapper around <see cref="TaskQueue{T}"/> that converts common exceptions into <see cref="Result{T}"/> failures.
/// </summary>
/// <typeparam name="T">Work item type.</typeparam>
/// <remarks>
/// Initializes a new instance of the <see cref="SafeTaskQueueWrapper{T}"/> class.
/// </remarks>
/// <param name="queue">Underlying task queue to wrap.</param>
/// <param name="ownsQueue">True to dispose the underlying queue when this wrapper is disposed.</param>
public sealed class SafeTaskQueueWrapper<T>(TaskQueue<T> queue, bool ownsQueue = false) : IAsyncDisposable
{
    private readonly TaskQueue<T> _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    private readonly bool _ownsQueue = ownsQueue;

    /// <summary>
    /// Gets the underlying queue for scenarios that require direct access.
    /// </summary>
    public TaskQueue<T> UnsafeQueue => _queue;

    /// <summary>Enqueues the supplied work item, returning a failure result instead of throwing when the operation cannot be completed.</summary>
    /// <param name="value">The work item to enqueue.</param>
    /// <param name="cancellationToken">The token used to cancel the enqueue operation.</param>
    /// <returns>A result indicating enqueue success or failure.</returns>
    public async ValueTask<Result<Unit>> EnqueueAsync(T value, CancellationToken cancellationToken = default) => await Result
            .TryAsync<Unit>(
                async ct =>
                {
                    await _queue.EnqueueAsync(value, ct).ConfigureAwait(false);
                    return Unit.Value;
                },
                errorFactory: MapQueueExceptions,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    /// <summary>Leases the next available work item.</summary>
    /// <param name="cancellationToken">The token used to cancel the lease operation.</param>
    /// <returns>A result containing the leased work item when successful.</returns>
    public async ValueTask<Result<SafeTaskQueueLease<T>>> LeaseAsync(CancellationToken cancellationToken = default)
    {
        Result<TaskQueueLease<T>> leaseResult = await Result
            .TryAsync<TaskQueueLease<T>>(
                async ct => await _queue.LeaseAsync(ct).ConfigureAwait(false),
                errorFactory: MapQueueExceptions,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return leaseResult.IsFailure
            ? Result.Fail<SafeTaskQueueLease<T>>(leaseResult.Error!)
            : Result.Ok(new SafeTaskQueueLease<T>(leaseResult.Value));
    }

    /// <summary>Creates a safe wrapper around an existing lease instance.</summary>
    /// <param name="lease">The lease to wrap.</param>
    /// <returns>A safe lease wrapper.</returns>
    public SafeTaskQueueLease<T> Wrap(TaskQueueLease<T> lease) => new(lease);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_ownsQueue)
        {
            await _queue.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static Error? MapQueueExceptions(Exception exception) =>
        exception switch
        {
            ObjectDisposedException => SafeTaskQueueErrors.QueueDisposed(),
            _ => null
        };
}

/// <summary>
/// Provides result-based helpers for managing a leased work item.
/// </summary>
/// <typeparam name="T">Work item type.</typeparam>
public sealed class SafeTaskQueueLease<T>(TaskQueueLease<T> lease)
{
    private readonly TaskQueueLease<T> _lease = lease ?? throw new ArgumentNullException(nameof(lease));

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
    /// Gets the ownership token associated with the lease.
    /// </summary>
    public TaskQueueOwnershipToken OwnershipToken => _lease.OwnershipToken;

    /// <summary>
    /// Gets the monotonic sequence identifier assigned when the work item was enqueued.
    /// </summary>
    public long SequenceId => _lease.SequenceId;

    /// <summary>Completes the lease.</summary>
    /// <param name="cancellationToken">The token used to cancel the completion.</param>
    /// <returns>A result indicating completion success or failure.</returns>
    public ValueTask<Result<Unit>> CompleteAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(ct => _lease.CompleteAsync(ct), cancellationToken);

    /// <summary>
    /// Sends a heartbeat to extend the lease duration.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the heartbeat.</param>
    /// <returns>A result indicating heartbeat success or failure.</returns>
    public ValueTask<Result<Unit>> HeartbeatAsync(CancellationToken cancellationToken = default) =>
        ExecuteAsync(ct => _lease.HeartbeatAsync(ct), cancellationToken);

    /// <summary>
    /// Fails the lease and optionally re-queues the work item.
    /// </summary>
    /// <param name="error">The error that caused the failure.</param>
    /// <param name="requeue"><see langword="true"/> to re-queue the work item; otherwise <see langword="false"/>.</param>
    /// <param name="cancellationToken">The token used to cancel the failure operation.</param>
    /// <returns>A result indicating failure success or failure.</returns>
    public ValueTask<Result<Unit>> FailAsync(Error? error, bool requeue = true, CancellationToken cancellationToken = default)
    {
        if (error is null)
        {
            return ValueTask.FromResult(Result.Fail<Unit>(Error.From("Error must be provided when failing a lease.", ErrorCodes.Validation)));
        }

        return ExecuteAsync(ct => _lease.FailAsync(error, requeue, ct), cancellationToken);
    }

    private static async ValueTask<Result<Unit>> ExecuteAsync(Func<CancellationToken, ValueTask> operation, CancellationToken cancellationToken = default) =>
        await Result
            .TryAsync<Unit>(
                async ct =>
                {
                    await operation(ct).ConfigureAwait(false);
                    return Unit.Value;
                },
                errorFactory: MapLeaseExceptions,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

    private static Error? MapLeaseExceptions(Exception exception) =>
        exception switch
        {
            ObjectDisposedException => SafeTaskQueueErrors.QueueDisposed(),
            InvalidOperationException => SafeTaskQueueErrors.LeaseInactive(),
            _ => null
        };
}

internal static class SafeTaskQueueErrors
{
    internal static Error QueueDisposed() => Error.From("The task queue has been disposed.", ErrorCodes.TaskQueueDisposed);

    internal static Error LeaseInactive() => Error.From("Lease is no longer active.", ErrorCodes.TaskQueueLeaseInactive);
}
