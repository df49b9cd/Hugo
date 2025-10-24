using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Hugo;

/// <summary>
/// Configures the behavior of a <see cref="TaskQueue{T}"/>.
/// </summary>
public sealed class TaskQueueOptions
{
    private readonly int _capacity = 512;
    private readonly TimeSpan _leaseDuration = TimeSpan.FromSeconds(30);
    private readonly TimeSpan _heartbeatInterval = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _leaseSweepInterval = TimeSpan.FromSeconds(1);
    private readonly TimeSpan _requeueDelay = TimeSpan.Zero;
    private readonly int _maxDeliveryAttempts = 5;

    /// <summary>
    /// Gets or sets the channel capacity used to buffer queued work. Defaults to 512 items.
    /// </summary>
    public int Capacity
    {
        get => _capacity;
        init => _capacity = value > 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Capacity must be positive.");
    }

    /// <summary>
    /// Gets or sets the lease duration granted to workers when a task is leased. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan LeaseDuration
    {
        get => _leaseDuration;
        init => _leaseDuration = ValidatePositive(value, nameof(LeaseDuration));
    }

    /// <summary>
    /// Gets or sets the minimum interval between heartbeats for the same lease. Defaults to 5 seconds.
    /// </summary>
    public TimeSpan HeartbeatInterval
    {
        get => _heartbeatInterval;
        init
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Heartbeat interval cannot be negative.");
            }

            _heartbeatInterval = value;
        }
    }

    /// <summary>
    /// Gets or sets how frequently the queue scans for expired leases. Defaults to 1 second.
    /// </summary>
    public TimeSpan LeaseSweepInterval
    {
        get => _leaseSweepInterval;
        init => _leaseSweepInterval = ValidatePositive(value, nameof(LeaseSweepInterval));
    }

    /// <summary>
    /// Gets or sets the optional delay applied before re-queueing a failed lease. Defaults to zero delay.
    /// </summary>
    public TimeSpan RequeueDelay
    {
        get => _requeueDelay;
        init
        {
            if (value < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Requeue delay cannot be negative.");
            }

            _requeueDelay = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of delivery attempts before an item is dead-lettered. Defaults to 5 attempts.
    /// </summary>
    public int MaxDeliveryAttempts
    {
        get => _maxDeliveryAttempts;
        init => _maxDeliveryAttempts = value switch
        {
            < 1 => throw new ArgumentOutOfRangeException(nameof(value), value, "Max delivery attempts must be at least 1."),
            _ => value
        };
    }

    private static TimeSpan ValidatePositive(TimeSpan value, string propertyName)
    {
        if (value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(propertyName, value, "Value must be positive.");
        }

        return value;
    }
}

/// <summary>
/// Represents an in-flight lease for a queued task.
/// </summary>
public sealed class TaskQueueLease<T>
{
    private readonly TaskQueue<T> _queue;
    private readonly Guid _leaseId;
    private int _status; // 0 = active, 1 = completed, 2 = failed

    internal TaskQueueLease(TaskQueue<T> queue, Guid leaseId, T value, int attempt, DateTimeOffset enqueuedAt, Error? lastError)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _leaseId = leaseId;
        Value = value;
        Attempt = attempt;
        EnqueuedAt = enqueuedAt;
        LastError = lastError;
    }

    /// <summary>
    /// Gets the leased value.
    /// </summary>
    public T Value { get; }

    /// <summary>
    /// Gets the 1-based delivery attempt count for this lease.
    /// </summary>
    public int Attempt { get; }

    /// <summary>
    /// Gets the original enqueue timestamp associated with the leased value.
    /// </summary>
    public DateTimeOffset EnqueuedAt { get; }

    /// <summary>
    /// Gets the error captured from the previous attempt when the item was re-queued.
    /// </summary>
    public Error? LastError { get; }

    /// <summary>
    /// Completes the lease and permanently removes the work item from the queue.
    /// </summary>
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return Interlocked.CompareExchange(ref _status, 1, 0) != 0 ? throw new InvalidOperationException("Lease is no longer active.") : _queue.CompleteAsync(_leaseId, cancellationToken);
    }

    /// <summary>
    /// Sends a heartbeat to extend the lease expiration.
    /// </summary>
    public ValueTask HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        EnsureActive();
        return _queue.HeartbeatAsync(_leaseId, cancellationToken);
    }

    /// <summary>
    /// Fails the lease and optionally re-queues the work item for another attempt.
    /// </summary>
    public ValueTask FailAsync(Error error, bool requeue = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);

        EnsureActive();
        return Interlocked.CompareExchange(ref _status, 2, 0) != 0 ? throw new InvalidOperationException("Lease is no longer active.") : _queue.FailAsync(_leaseId, error, requeue, cancellationToken);
    }

    private void EnsureActive()
    {
        if (Volatile.Read(ref _status) != 0)
        {
            throw new InvalidOperationException("Lease is no longer active.");
        }
    }
}

/// <summary>
/// Provides details about a work item that was moved to the dead-letter sink.
/// </summary>
/// <param name="Value">The work item payload.</param>
/// <param name="Error">The error that caused dead-lettering.</param>
/// <param name="Attempt">The final attempt count.</param>
/// <param name="EnqueuedAt">The original enqueue timestamp.</param>
public readonly record struct TaskQueueDeadLetterContext<T>(T Value, Error Error, int Attempt, DateTimeOffset EnqueuedAt);

/// <summary>
/// Provides a channel-backed task queue with cooperative leasing and heartbeat semantics.
/// </summary>
public sealed class TaskQueue<T> : IAsyncDisposable
{
    private readonly TaskQueueOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TaskQueueDeadLetterContext<T>, CancellationToken, ValueTask>? _deadLetter;
    private readonly Channel<QueueEnvelope> _channel;
    private readonly ConcurrentDictionary<Guid, LeaseState> _leases = new();
    private readonly CancellationTokenSource _monitorCts = new();
    private readonly Task _monitorTask;
    private long _pendingCount;
    private int _disposed;

    public TaskQueue(TaskQueueOptions? options = null, TimeProvider? timeProvider = null, Func<TaskQueueDeadLetterContext<T>, CancellationToken, ValueTask>? deadLetter = null)
    {
        _options = options ?? new TaskQueueOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _deadLetter = deadLetter;

        BoundedChannelOptions channelOptions = new(_options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<QueueEnvelope>(channelOptions);
        _monitorTask = Task.Run(() => MonitorLeasesAsync(_monitorCts.Token));
    }

    /// <summary>
    /// Gets the number of items currently waiting to be leased.
    /// </summary>
    public long PendingCount => Math.Max(0, Volatile.Read(ref _pendingCount));

    /// <summary>
    /// Gets the number of active leases.
    /// </summary>
    public int ActiveLeaseCount => _leases.Count;

    /// <summary>
    /// Enqueues a work item for processing.
    /// </summary>
    public async ValueTask EnqueueAsync(T value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        QueueEnvelope envelope = new(value, 1, _timeProvider.GetUtcNow(), null);
        long depth = Interlocked.Increment(ref _pendingCount);

        try
        {
            await _channel.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            GoDiagnostics.RecordTaskQueueQueued(depth);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingCount);
            throw;
        }
    }

    /// <summary>
    /// Leases the next available work item, waiting if necessary.
    /// </summary>
    public async ValueTask<TaskQueueLease<T>> LeaseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        QueueEnvelope envelope;
        bool readSucceeded = false;
        try
        {
            envelope = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            readSucceeded = true;
        }
        catch (ChannelClosedException) when (IsDisposed)
        {
            throw new ObjectDisposedException(nameof(TaskQueue<T>));
        }
        finally
        {
            if (readSucceeded)
            {
                Interlocked.Decrement(ref _pendingCount);
            }
        }

        Guid leaseId = Guid.NewGuid();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset expiration = now + _options.LeaseDuration;
        LeaseState state = new(envelope, now, expiration);

        if (!_leases.TryAdd(leaseId, state))
        {
            throw new InvalidOperationException("Failed to track lease state.");
        }

        GoDiagnostics.RecordTaskQueueLeased(envelope.Attempt, PendingCount, _leases.Count);
        return new TaskQueueLease<T>(this, leaseId, envelope.Value, envelope.Attempt, envelope.EnqueuedAt, envelope.LastError);
    }

    internal ValueTask CompleteAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (!_leases.TryGetValue(leaseId, out LeaseState? state) || !state.TryComplete())
        {
            throw new InvalidOperationException("Lease is no longer active.");
        }

        _leases.TryRemove(leaseId, out _);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        GoDiagnostics.RecordTaskQueueCompleted(state.Envelope.Attempt, now - state.GrantedAt, PendingCount, _leases.Count);
        return ValueTask.CompletedTask;
    }

    internal ValueTask HeartbeatAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (!_leases.TryGetValue(leaseId, out LeaseState? state))
        {
            throw new InvalidOperationException("Lease is no longer active.");
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        if (_options.HeartbeatInterval > TimeSpan.Zero)
        {
            TimeSpan elapsed = now - state.LastHeartbeat;
            if (elapsed < _options.HeartbeatInterval)
            {
                return ValueTask.CompletedTask;
            }
        }

        if (!state.TryHeartbeat(now, _options.LeaseDuration, out DateTimeOffset newExpiration))
        {
            throw new InvalidOperationException("Lease is no longer active.");
        }

        GoDiagnostics.RecordTaskQueueHeartbeat(state.Envelope.Attempt, newExpiration - now, _leases.Count);
        return ValueTask.CompletedTask;
    }

    internal async ValueTask FailAsync(Guid leaseId, Error error, bool requeue, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (!_leases.TryGetValue(leaseId, out LeaseState? state) || !state.TryFail())
        {
            throw new InvalidOperationException("Lease is no longer active.");
        }

        _leases.TryRemove(leaseId, out _);

        DateTimeOffset now = _timeProvider.GetUtcNow();
        GoDiagnostics.RecordTaskQueueFailed(state.Envelope.Attempt, now - state.GrantedAt, PendingCount, _leases.Count);

        if (!requeue)
        {
            await DeadLetterAsync(state.Envelope, error, cancellationToken).ConfigureAwait(false);
            return;
        }

        await RequeueAsync(state.Envelope, error, cancellationToken).ConfigureAwait(false);
    }

    private async Task MonitorLeasesAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await _timeProvider.DelayAsync(_options.LeaseSweepInterval, cancellationToken).ConfigureAwait(false);
                await SweepExpiredLeasesAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // expected during shutdown
        }
    }

    private async ValueTask SweepExpiredLeasesAsync(CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        List<(Guid Id, LeaseState State)> expired = [];

        foreach (KeyValuePair<Guid, LeaseState> kvp in _leases)
        {
            if (kvp.Value.TryExpire(now))
            {
                expired.Add((kvp.Key, kvp.Value));
            }
        }

        if (expired.Count == 0)
        {
            return;
        }

        foreach ((Guid id, LeaseState state) in expired)
        {
            if (!_leases.TryRemove(new KeyValuePair<Guid, LeaseState>(id, state)))
            {
                continue;
            }

            Dictionary<string, object?> metadata = new(StringComparer.OrdinalIgnoreCase)
            {
                ["attempt"] = state.Envelope.Attempt,
                ["enqueuedAt"] = state.Envelope.EnqueuedAt,
                ["expiredAt"] = now
            };

            Error expiredError = Error.From("Lease expired without heartbeat.", ErrorCodes.TaskQueueLeaseExpired)
                .WithMetadata(metadata);

            GoDiagnostics.RecordTaskQueueExpired(state.Envelope.Attempt, now - state.GrantedAt, PendingCount, _leases.Count);
            await RequeueAsync(state.Envelope, expiredError, cancellationToken).ConfigureAwait(false);
        }
    }

    private async ValueTask RequeueAsync(QueueEnvelope envelope, Error error, CancellationToken cancellationToken)
    {
        int nextAttempt = envelope.Attempt + 1;
        if (nextAttempt > _options.MaxDeliveryAttempts)
        {
            await DeadLetterAsync(envelope, error, cancellationToken).ConfigureAwait(false);
            return;
        }

        QueueEnvelope requeued = new(envelope.Value, nextAttempt, envelope.EnqueuedAt, error);

        if (_options.RequeueDelay > TimeSpan.Zero)
        {
            await _timeProvider.DelayAsync(_options.RequeueDelay, cancellationToken).ConfigureAwait(false);
        }

        long depth = Interlocked.Increment(ref _pendingCount);

        try
        {
            await _channel.Writer.WriteAsync(requeued, cancellationToken).ConfigureAwait(false);
            GoDiagnostics.RecordTaskQueueRequeued(nextAttempt, depth, _leases.Count);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingCount);
            throw;
        }
    }

    private async ValueTask DeadLetterAsync(QueueEnvelope envelope, Error error, CancellationToken cancellationToken)
    {
        GoDiagnostics.RecordTaskQueueDeadLettered(envelope.Attempt, PendingCount, _leases.Count);

        if (_deadLetter is null)
        {
            return;
        }

        TaskQueueDeadLetterContext<T> context = new(envelope.Value, error, envelope.Attempt, envelope.EnqueuedAt);
        await _deadLetter(context, cancellationToken).ConfigureAwait(false);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, nameof(TaskQueue<T>));

    private bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _monitorCts.CancelAsync().ConfigureAwait(false);
        _channel.Writer.TryComplete();

        try
        {
            await _monitorTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // ignored during shutdown
        }

        _monitorCts.Dispose();
    }

    private sealed record QueueEnvelope(T Value, int Attempt, DateTimeOffset EnqueuedAt, Error? LastError);

    private sealed class LeaseState
    {
        private readonly Lock _sync = new();
        private int _status; // 0 = active, 1 = completed, 2 = failed, 3 = expired

        internal LeaseState(QueueEnvelope envelope, DateTimeOffset grantedAt, DateTimeOffset expiration)
        {
            Envelope = envelope;
            GrantedAt = grantedAt;
            Expiration = expiration;
            LastHeartbeat = grantedAt;
        }

        internal QueueEnvelope Envelope { get; }

        internal DateTimeOffset GrantedAt { get; }

        internal DateTimeOffset Expiration { get; private set; }

        internal DateTimeOffset LastHeartbeat { get; private set; }

        internal bool TryHeartbeat(DateTimeOffset now, TimeSpan leaseDuration, out DateTimeOffset newExpiration)
        {
            lock (_sync)
            {
                if (_status != 0)
                {
                    newExpiration = Expiration;
                    return false;
                }

                LastHeartbeat = now;
                Expiration = newExpiration = now + leaseDuration;
                return true;
            }
        }

        internal bool TryComplete()
        {
            lock (_sync)
            {
                if (_status != 0)
                {
                    return false;
                }

                _status = 1;
                return true;
            }
        }

        internal bool TryFail()
        {
            lock (_sync)
            {
                if (_status != 0)
                {
                    return false;
                }

                _status = 2;
                return true;
            }
        }

        internal bool TryExpire(DateTimeOffset now)
        {
            lock (_sync)
            {
                if (_status != 0 || now < Expiration)
                {
                    return false;
                }

                _status = 3;
                return true;
            }
        }
    }
}
