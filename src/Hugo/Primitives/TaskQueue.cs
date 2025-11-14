using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

using Hugo.TaskQueues;

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
    private string? _name;

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

    /// <summary>
    /// Gets or sets an optional queue name used for diagnostics and tracing.
    /// </summary>
    public string? Name
    {
        get => _name;
        init => _name = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>
    /// Gets or sets backpressure configuration applied to the queue.
    /// </summary>
    public TaskQueueBackpressureOptions? Backpressure { get; init; }

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
/// Configures queue-level backpressure notifications.
/// </summary>
public sealed class TaskQueueBackpressureOptions
{
    private long _highWatermark = -1;
    private long _lowWatermark = -1;
    private TimeSpan _cooldown = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the backlog size that triggers backpressure. Specify a negative value to disable.
    /// </summary>
    public long HighWatermark
    {
        get => _highWatermark;
        init
        {
            if (value < 0)
            {
                _highWatermark = -1;
                return;
            }

            if (value == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "High watermark must be greater than zero when enabled.");
            }

            _highWatermark = value;
        }
    }

    /// <summary>
    /// Gets or sets the backlog size that clears backpressure. Defaults to <see cref="HighWatermark"/>.
    /// </summary>
    public long LowWatermark
    {
        get => _lowWatermark < 0 && _highWatermark > 0 ? _highWatermark / 2 : _lowWatermark;
        init
        {
            if (value < 0)
            {
                _lowWatermark = -1;
                return;
            }

            _lowWatermark = value;
        }
    }

    /// <summary>
    /// Gets or sets the minimum duration between repeated notifications for the same state.
    /// </summary>
    public TimeSpan Cooldown
    {
        get => _cooldown;
        init
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Cooldown must be positive.");
            }

            _cooldown = value;
        }
    }

    /// <summary>
    /// Gets or sets the callback invoked when the backpressure state changes.
    /// </summary>
    public Action<TaskQueueBackpressureState>? StateChanged { get; init; }
}

/// <summary>
/// Represents the current backpressure state.
/// </summary>
/// <param name="IsActive">True when writers should throttle.</param>
/// <param name="PendingCount">The pending depth that triggered the event.</param>
/// <param name="ObservedAt">Timestamp associated with the observation.</param>
public readonly record struct TaskQueueBackpressureState(bool IsActive, long PendingCount, DateTimeOffset ObservedAt);

/// <summary>
/// Represents an in-flight lease for a queued task.
/// </summary>
public sealed class TaskQueueLease<T>
{
    private readonly TaskQueue<T> _queue;
    private readonly Guid _leaseId;
    private readonly long _sequenceId;
    private readonly TaskQueueOwnershipToken _ownershipToken;

    internal TaskQueueLease(TaskQueue<T> queue, Guid leaseId, long sequenceId, T value, int attempt, DateTimeOffset enqueuedAt, Error? lastError)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _leaseId = leaseId;
        _sequenceId = sequenceId;
        Value = value;
        Attempt = attempt;
        EnqueuedAt = enqueuedAt;
        LastError = lastError;
        _ownershipToken = new TaskQueueOwnershipToken(sequenceId, attempt, leaseId);
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
    /// Gets the monotonic ownership token associated with the current lease.
    /// </summary>
    public TaskQueueOwnershipToken OwnershipToken => _ownershipToken;

    /// <summary>
    /// Gets the enqueue sequence identifier used for fencing and tracing.
    /// </summary>
    public long SequenceId => _sequenceId;

    /// <summary>Completes the lease and permanently removes the work item from the queue.</summary>
    /// <param name="cancellationToken">The token used to cancel the completion.</param>
    /// <returns>A task that completes once the lease is acknowledged.</returns>
    public ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        return _queue.CompleteAsync(_leaseId, cancellationToken);
    }

    /// <summary>Sends a heartbeat to extend the lease expiration.</summary>
    /// <param name="cancellationToken">The token used to cancel the heartbeat.</param>
    /// <returns>A task that completes once the heartbeat is acknowledged.</returns>
    public ValueTask HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        return _queue.HeartbeatAsync(_leaseId, cancellationToken);
    }

    /// <summary>Fails the lease and optionally re-queues the work item for another attempt.</summary>
    /// <param name="error">The error that caused the failure.</param>
    /// <param name="requeue"><see langword="true"/> to re-queue the work item; otherwise <see langword="false"/>.</param>
    /// <param name="cancellationToken">The token used to cancel the failure operation.</param>
    /// <returns>A task that completes once the failure is acknowledged.</returns>
    public ValueTask FailAsync(Error error, bool requeue = true, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);

        return _queue.FailAsync(_leaseId, error, requeue, cancellationToken);
    }
}

/// <summary>
/// Represents the ownership token associated with a leased work item.
/// </summary>
/// <param name="SequenceId">Monotonically increasing identifier assigned at enqueue time.</param>
/// <param name="Attempt">The delivery attempt represented by the token.</param>
/// <param name="LeaseId">Unique lease identifier.</param>
public readonly record struct TaskQueueOwnershipToken(long SequenceId, int Attempt, Guid LeaseId)
{
    /// <inheritdoc />
    public override string ToString() => $"{SequenceId:D}:{Attempt}:{LeaseId:N}";
}

/// <summary>
/// Provides details about a work item that was moved to the dead-letter sink.
/// </summary>
/// <param name="Value">The work item payload.</param>
/// <param name="Error">The error that caused dead-lettering.</param>
/// <param name="Attempt">The final attempt count.</param>
/// <param name="EnqueuedAt">The original enqueue timestamp.</param>
public readonly record struct TaskQueueDeadLetterContext<T>(
    T Value,
    Error Error,
    int Attempt,
    DateTimeOffset EnqueuedAt,
    long SequenceId,
    TaskQueueOwnershipToken? LastOwnershipToken);

/// <summary>
/// Represents pending work captured during queue draining.
/// </summary>
/// <param name="Value">The work item payload.</param>
/// <param name="Attempt">The attempt that had not yet completed.</param>
/// <param name="EnqueuedAt">The enqueue timestamp.</param>
/// <param name="LastError">The last error recorded for the payload.</param>
/// <param name="SequenceId">Monotonically increasing sequence identifier.</param>
/// <param name="LastOwnershipToken">The last ownership token observed for the payload.</param>
public readonly record struct TaskQueuePendingItem<T>(
    T Value,
    int Attempt,
    DateTimeOffset EnqueuedAt,
    Error? LastError,
    long SequenceId,
    TaskQueueOwnershipToken? LastOwnershipToken);

/// <summary>
/// Provides a channel-backed task queue with cooperative leasing and heartbeat semantics.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "TaskQueue exposes queue semantics without deriving from System.Collections.Queue by design.")]
public sealed class TaskQueue<T> : IAsyncDisposable
{
    private readonly TaskQueueOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly Func<TaskQueueDeadLetterContext<T>, CancellationToken, ValueTask>? _deadLetter;
    private readonly Channel<QueueEnvelope> _channel;
    private readonly ConcurrentDictionary<Guid, LeaseState> _leases = new();
    private readonly CancellationTokenSource _monitorCts = new();
    private readonly Task _monitorTask;
    private readonly string? _queueName;
    private TaskQueueLifecycleDispatcher<T>? _lifecycleDispatcher;
    private TaskQueueBackpressureOptions? _backpressureOptions;
    private long _pendingCount;
    private int _activeLeaseCount;
    private long _sequenceCounter;
    private int _backpressureState;
    private long _lastBackpressureNotificationTicks = DateTimeOffset.MinValue.UtcTicks;
    private int _disposed;

    /// <summary>Initializes a new instance of the <see cref="TaskQueue{T}"/> class.</summary>
    /// <param name="options">The configuration applied to the queue.</param>
    /// <param name="timeProvider">The time provider used for lease timing.</param>
    /// <param name="deadLetter">An optional handler invoked when an item is dead-lettered.</param>
    public TaskQueue(TaskQueueOptions? options = null, TimeProvider? timeProvider = null, Func<TaskQueueDeadLetterContext<T>, CancellationToken, ValueTask>? deadLetter = null)
    {
        _options = options ?? new TaskQueueOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _deadLetter = deadLetter;
        _queueName = string.IsNullOrWhiteSpace(_options.Name) ? typeof(T).Name : _options.Name;
        _backpressureOptions = _options.Backpressure;

        BoundedChannelOptions channelOptions = new(_options.Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        };

        _channel = Channel.CreateBounded<QueueEnvelope>(channelOptions);
        _monitorTask = Go.Run(() => MonitorLeasesAsync(_monitorCts.Token));
    }

    /// <summary>
    /// Gets the number of items currently waiting to be leased.
    /// </summary>
    public long PendingCount => Math.Max(0, Volatile.Read(ref _pendingCount));

    /// <summary>
    /// Gets the number of active leases.
    /// </summary>
    public int ActiveLeaseCount => Math.Max(0, Volatile.Read(ref _activeLeaseCount));

    /// <summary>
    /// Gets the configured queue name used for diagnostics and instrumentation.
    /// </summary>
    public string QueueName => _queueName ?? typeof(T).Name;

    /// <summary>
    /// Registers a lifecycle listener that observes queue mutations.
    /// </summary>
    /// <param name="listener">Listener to register.</param>
    /// <returns>An <see cref="IDisposable"/> used to unregister the listener.</returns>
    public IDisposable RegisterLifecycleListener(ITaskQueueLifecycleListener<T> listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        return EnsureLifecycleDispatcher().Register(listener);
    }

    /// <summary>
    /// Updates the queue-level backpressure configuration at runtime.
    /// </summary>
    /// <param name="options">The backpressure options to apply, or <see langword="null"/> to disable notifications.</param>
    public void ConfigureBackpressure(TaskQueueBackpressureOptions? options)
    {
        Volatile.Write(ref _backpressureOptions, options);
        Volatile.Write(ref _backpressureState, 0);
        Volatile.Write(ref _lastBackpressureNotificationTicks, DateTimeOffset.MinValue.UtcTicks);

        if (options is null)
        {
            return;
        }

        ObserveBackpressure(Math.Max(0, Volatile.Read(ref _pendingCount)));
    }

    /// <summary>Enqueues a work item for processing.</summary>
    /// <param name="value">The work item to enqueue.</param>
    /// <param name="cancellationToken">The token used to cancel the enqueue operation.</param>
    public async ValueTask EnqueueAsync(T value, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        DateTimeOffset enqueuedAt = _timeProvider.GetUtcNow();
        long sequenceId = Interlocked.Increment(ref _sequenceCounter);
        QueueEnvelope envelope = new(value, sequenceId, 1, enqueuedAt, null, null);
        long depth = Interlocked.Increment(ref _pendingCount);
        using Activity? activity = GoDiagnostics.StartTaskQueueActivity("enqueue", _queueName, sequenceId, 1);

        try
        {
            await _channel.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
            GoDiagnostics.RecordTaskQueueQueued(_queueName, depth);
            ObserveBackpressure(depth);
            GoDiagnostics.CompleteTaskQueueActivity(activity);
            PublishLifecycleEvent(TaskQueueLifecycleEventKind.Enqueued, envelope, occurrence: enqueuedAt);
        }
        catch (Exception ex)
        {
            Interlocked.Decrement(ref _pendingCount);
            GoDiagnostics.CompleteTaskQueueActivity(activity, Error.FromException(ex));
            throw;
        }
    }

    /// <summary>Leases the next available work item, waiting if necessary.</summary>
    /// <param name="cancellationToken">The token used to cancel the lease operation.</param>
    /// <returns>An active lease that owns the work item.</returns>
    public async ValueTask<TaskQueueLease<T>> LeaseAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        QueueEnvelope envelope = default!;
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
                ObserveBackpressure(Math.Max(0, Volatile.Read(ref _pendingCount)));
            }
        }

        using Activity? activity = GoDiagnostics.StartTaskQueueActivity("lease", _queueName, envelope.SequenceId, envelope.Attempt);

        Guid leaseId = Guid.NewGuid();
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTimeOffset expiration = now + _options.LeaseDuration;
        LeaseState state = new(envelope, leaseId, now, expiration);

        if (!_leases.TryAdd(leaseId, state))
        {
            var error = Error.From("Failed to track lease state.", ErrorCodes.TaskQueueLeaseInactive);
            GoDiagnostics.CompleteTaskQueueActivity(activity, error);
            throw new InvalidOperationException("Failed to track lease state.");
        }

        Interlocked.Increment(ref _activeLeaseCount);

        GoDiagnostics.RecordTaskQueueLeased(_queueName, envelope.Attempt, PendingCount, ActiveLeaseCount);
        GoDiagnostics.CompleteTaskQueueActivity(activity);
        PublishLifecycleEvent(TaskQueueLifecycleEventKind.LeaseGranted, state.Envelope, state.OwnershipToken, leaseExpiration: expiration, occurrence: now);
        return new TaskQueueLease<T>(this, leaseId, envelope.SequenceId, envelope.Value, envelope.Attempt, envelope.EnqueuedAt, envelope.LastError);
    }

    internal ValueTask CompleteAsync(Guid leaseId, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (!_leases.TryGetValue(leaseId, out LeaseState? state) || !state.TryComplete())
        {
            throw new InvalidOperationException("Lease is no longer active.");
        }

        if (_leases.TryRemove(leaseId, out _))
        {
            Interlocked.Decrement(ref _activeLeaseCount);
        }
        DateTimeOffset now = _timeProvider.GetUtcNow();
        using Activity? activity = GoDiagnostics.StartTaskQueueActivity(
            "complete",
            _queueName,
            state.Envelope.SequenceId,
            state.Envelope.Attempt,
            state.OwnershipToken);
        GoDiagnostics.RecordTaskQueueCompleted(_queueName, state.Envelope.Attempt, now - state.GrantedAt, PendingCount, ActiveLeaseCount);
        GoDiagnostics.CompleteTaskQueueActivity(activity);
        PublishLifecycleEvent(TaskQueueLifecycleEventKind.Completed, state.Envelope, state.OwnershipToken, occurrence: now);
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

        using Activity? activity = GoDiagnostics.StartTaskQueueActivity(
            "heartbeat",
            _queueName,
            state.Envelope.SequenceId,
            state.Envelope.Attempt,
            state.OwnershipToken);

        if (!state.TryHeartbeat(now, _options.LeaseDuration, out DateTimeOffset newExpiration))
        {
            GoDiagnostics.CompleteTaskQueueActivity(activity, Error.From("Lease is no longer active.", ErrorCodes.TaskQueueLeaseInactive));
            throw new InvalidOperationException("Lease is no longer active.");
        }

        GoDiagnostics.RecordTaskQueueHeartbeat(_queueName, state.Envelope.Attempt, newExpiration - now, ActiveLeaseCount);
        GoDiagnostics.CompleteTaskQueueActivity(activity);
        PublishLifecycleEvent(TaskQueueLifecycleEventKind.Heartbeat, state.Envelope, state.OwnershipToken, leaseExpiration: newExpiration, occurrence: now);
        return ValueTask.CompletedTask;
    }

    internal async ValueTask FailAsync(Guid leaseId, Error error, bool requeue, CancellationToken cancellationToken)
    {
        ThrowIfDisposed();

        if (!_leases.TryGetValue(leaseId, out LeaseState? state) || !state.TryFail())
        {
            throw new InvalidOperationException("Lease is no longer active.");
        }

        if (_leases.TryRemove(leaseId, out _))
        {
            Interlocked.Decrement(ref _activeLeaseCount);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        GoDiagnostics.RecordTaskQueueFailed(_queueName, state.Envelope.Attempt, now - state.GrantedAt, PendingCount, ActiveLeaseCount);
        QueueEnvelope envelopeWithToken = state.Envelope with { LastOwnershipToken = state.OwnershipToken };
        PublishLifecycleEvent(TaskQueueLifecycleEventKind.Failed, envelopeWithToken, state.OwnershipToken, error);

        using Activity? activity = GoDiagnostics.StartTaskQueueActivity(
            requeue ? "fail.requeue" : "fail.deadletter",
            _queueName,
            envelopeWithToken.SequenceId,
            envelopeWithToken.Attempt,
            state.OwnershipToken);

        if (!requeue)
        {
            await DeadLetterAsync(envelopeWithToken, error, cancellationToken).ConfigureAwait(false);
            GoDiagnostics.CompleteTaskQueueActivity(activity, error);
            return;
        }

        await RequeueAsync(envelopeWithToken, error, state.OwnershipToken, cancellationToken).ConfigureAwait(false);
        GoDiagnostics.CompleteTaskQueueActivity(activity, error);
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

        foreach (KeyValuePair<Guid, LeaseState> kvp in _leases)
        {
            LeaseState state = kvp.Value;
            if (!state.TryExpire(now))
            {
                continue;
            }

            if (!_leases.TryRemove(new KeyValuePair<Guid, LeaseState>(kvp.Key, state)))
            {
                continue;
            }

            Interlocked.Decrement(ref _activeLeaseCount);

            Dictionary<string, object?> metadata = new(3, StringComparer.OrdinalIgnoreCase)
            {
                ["attempt"] = state.Envelope.Attempt,
                ["enqueuedAt"] = state.Envelope.EnqueuedAt,
                ["expiredAt"] = now
            };

            Error expiredError = Error.From("Lease expired without heartbeat.", ErrorCodes.TaskQueueLeaseExpired)
                .WithMetadata(metadata);

            GoDiagnostics.RecordTaskQueueExpired(_queueName, state.Envelope.Attempt, now - state.GrantedAt, PendingCount, ActiveLeaseCount);
            QueueEnvelope expiredEnvelope = state.Envelope with { LastOwnershipToken = state.OwnershipToken };
            PublishLifecycleEvent(TaskQueueLifecycleEventKind.Expired, expiredEnvelope, state.OwnershipToken, expiredError, occurrence: now, flags: TaskQueueLifecycleEventMetadata.FromExpiration);
            await RequeueAsync(expiredEnvelope, expiredError, state.OwnershipToken, cancellationToken, TaskQueueLifecycleEventMetadata.FromExpiration).ConfigureAwait(false);
        }
    }

    private async ValueTask RequeueAsync(
        QueueEnvelope envelope,
        Error error,
        TaskQueueOwnershipToken? ownershipToken,
        CancellationToken cancellationToken,
        TaskQueueLifecycleEventMetadata flags = TaskQueueLifecycleEventMetadata.None)
    {
        int nextAttempt = envelope.Attempt + 1;
        if (nextAttempt > _options.MaxDeliveryAttempts)
        {
            await DeadLetterAsync(envelope, error, cancellationToken).ConfigureAwait(false);
            return;
        }

        TaskQueueOwnershipToken? recordedToken = ownershipToken ?? envelope.LastOwnershipToken;
        QueueEnvelope requeued = new(envelope.Value, envelope.SequenceId, nextAttempt, envelope.EnqueuedAt, error, recordedToken);

        if (_options.RequeueDelay > TimeSpan.Zero)
        {
            try
            {
                await _timeProvider.DelayAsync(_options.RequeueDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Skip the delay when cancellation is requested but continue requeueing to avoid data loss.
            }
        }

        long depth = Interlocked.Increment(ref _pendingCount);

        try
        {
            try
            {
                await _channel.Writer.WriteAsync(requeued, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await _channel.Writer.WriteAsync(requeued, CancellationToken.None).ConfigureAwait(false);
            }

            GoDiagnostics.RecordTaskQueueRequeued(_queueName, nextAttempt, depth, ActiveLeaseCount);
            ObserveBackpressure(depth);
            PublishLifecycleEvent(TaskQueueLifecycleEventKind.Requeued, requeued, recordedToken, error, flags: flags);
        }
        catch (ChannelClosedException)
        {
            Interlocked.Decrement(ref _pendingCount);
            await DeadLetterAsync(requeued, error, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Interlocked.Decrement(ref _pendingCount);
            throw;
        }
    }

    private async ValueTask DeadLetterAsync(QueueEnvelope envelope, Error error, CancellationToken cancellationToken)
    {
        GoDiagnostics.RecordTaskQueueDeadLettered(_queueName, envelope.Attempt, PendingCount, ActiveLeaseCount);

        if (_deadLetter is null)
        {
            PublishLifecycleEvent(TaskQueueLifecycleEventKind.DeadLettered, envelope, envelope.LastOwnershipToken, error, flags: TaskQueueLifecycleEventMetadata.DeadLetter);
            return;
        }

        TaskQueueDeadLetterContext<T> context = new(envelope.Value, error, envelope.Attempt, envelope.EnqueuedAt, envelope.SequenceId, envelope.LastOwnershipToken);
        await _deadLetter(context, CancellationToken.None).ConfigureAwait(false);
        PublishLifecycleEvent(TaskQueueLifecycleEventKind.DeadLettered, envelope, envelope.LastOwnershipToken, error, flags: TaskQueueLifecycleEventMetadata.DeadLetter);
    }

    private TaskQueueLifecycleDispatcher<T> EnsureLifecycleDispatcher()
    {
        TaskQueueLifecycleDispatcher<T>? dispatcher = Volatile.Read(ref _lifecycleDispatcher);
        if (dispatcher is not null)
        {
            return dispatcher;
        }

        TaskQueueLifecycleDispatcher<T> created = new();
        TaskQueueLifecycleDispatcher<T>? existing = Interlocked.CompareExchange(ref _lifecycleDispatcher, created, null);
        return existing ?? created;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(IsDisposed, nameof(TaskQueue<T>));

    private bool IsDisposed => Volatile.Read(ref _disposed) == 1;

    /// <summary>Disposes the queue and drains any pending work.</summary>
    public async ValueTask DisposeAsync()
    {
        List<QueueEnvelope> drained = await ShutdownAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (QueueEnvelope envelope in drained)
        {
            Error reason = envelope.LastError ?? Error.From("Queue disposed before delivery.", ErrorCodes.TaskQueueDeadLettered);
            await DeadLetterAsync(envelope, reason, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Drains pending work for persistence prior to shutdown.
    /// </summary>
    public async ValueTask<IReadOnlyList<TaskQueuePendingItem<T>>> DrainPendingItemsAsync(CancellationToken cancellationToken = default)
    {
        List<QueueEnvelope> drained = await ShutdownAsync(cancellationToken).ConfigureAwait(false);
        if (drained.Count == 0)
        {
            return Array.Empty<TaskQueuePendingItem<T>>();
        }

        TaskQueuePendingItem<T>[] snapshot = new TaskQueuePendingItem<T>[drained.Count];
        for (int i = 0; i < drained.Count; i++)
        {
            snapshot[i] = ToPendingItem(drained[i]);
        }

        foreach (QueueEnvelope envelope in drained)
        {
            PublishLifecycleEvent(TaskQueueLifecycleEventKind.Drained, envelope, envelope.LastOwnershipToken, envelope.LastError, flags: TaskQueueLifecycleEventMetadata.FromDrain);
        }

        return snapshot;
    }

    /// <summary>
    /// Restores previously drained work items into the queue.
    /// </summary>
    public async ValueTask RestorePendingItemsAsync(IEnumerable<TaskQueuePendingItem<T>> pendingItems, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(pendingItems);

        foreach (TaskQueuePendingItem<T> pending in pendingItems)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AdvanceSequenceCounter(pending.SequenceId);

            QueueEnvelope envelope = new(
                pending.Value,
                pending.SequenceId,
                pending.Attempt,
                pending.EnqueuedAt,
                pending.LastError,
                pending.LastOwnershipToken);

            long depth = Interlocked.Increment(ref _pendingCount);

            try
            {
                await _channel.Writer.WriteAsync(envelope, cancellationToken).ConfigureAwait(false);
                GoDiagnostics.RecordTaskQueueQueued(_queueName, depth);
                ObserveBackpressure(depth);
            }
            catch
            {
                Interlocked.Decrement(ref _pendingCount);
                throw;
            }
        }
    }

    private async ValueTask<List<QueueEnvelope>> ShutdownAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return new List<QueueEnvelope>(0);
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
        return await DrainPendingCoreAsync(cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<List<QueueEnvelope>> DrainPendingCoreAsync(CancellationToken cancellationToken)
    {
        List<QueueEnvelope> drained = new();
        while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_channel.Reader.TryRead(out QueueEnvelope? envelope))
            {
                Interlocked.Decrement(ref _pendingCount);
                drained.Add(envelope);
            }
        }

        return drained;
    }

    private static TaskQueuePendingItem<T> ToPendingItem(QueueEnvelope envelope) =>
        new(envelope.Value, envelope.Attempt, envelope.EnqueuedAt, envelope.LastError, envelope.SequenceId, envelope.LastOwnershipToken);

    private void AdvanceSequenceCounter(long candidate)
    {
        while (true)
        {
            long current = Volatile.Read(ref _sequenceCounter);
            if (candidate <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _sequenceCounter, candidate, current) == current)
            {
                return;
            }
        }
    }

    private void ObserveBackpressure(long pendingDepth)
    {
        if (pendingDepth < 0)
        {
            return;
        }

        if (!TryGetBackpressureConfiguration(out long high, out long low, out TimeSpan cooldown, out Action<TaskQueueBackpressureState>? callback))
        {
            return;
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        bool currentlyActive = Volatile.Read(ref _backpressureState) == 1;

        if (!currentlyActive && pendingDepth >= high)
        {
            if (Interlocked.CompareExchange(ref _backpressureState, 1, 0) == 0 && ShouldEmitBackpressure(now, cooldown))
            {
                callback?.Invoke(new TaskQueueBackpressureState(true, pendingDepth, now));
            }

            return;
        }

        if (currentlyActive && pendingDepth <= low)
        {
            if (Interlocked.CompareExchange(ref _backpressureState, 0, 1) == 1 && ShouldEmitBackpressure(now, cooldown))
            {
                callback?.Invoke(new TaskQueueBackpressureState(false, pendingDepth, now));
            }
        }
    }

    private bool TryGetBackpressureConfiguration(
        out long high,
        out long low,
        out TimeSpan cooldown,
        out Action<TaskQueueBackpressureState>? callback)
    {
        TaskQueueBackpressureOptions? options = Volatile.Read(ref _backpressureOptions);
        if (options is null || options.HighWatermark <= 0)
        {
            high = low = 0;
            cooldown = TimeSpan.Zero;
            callback = null;
            return false;
        }

        high = options.HighWatermark;
        long configuredLow = options.LowWatermark;
        low = configuredLow <= 0 || configuredLow >= high ? Math.Max(1, high / 2) : configuredLow;
        cooldown = options.Cooldown;
        callback = options.StateChanged;
        return callback is not null;
    }

    private bool ShouldEmitBackpressure(DateTimeOffset now, TimeSpan cooldown)
    {
        long current = now.UtcTicks;
        while (true)
        {
            long last = Volatile.Read(ref _lastBackpressureNotificationTicks);
            if (current - last < cooldown.Ticks)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _lastBackpressureNotificationTicks, current, last) == last)
            {
                return true;
            }
        }
    }

    private void PublishLifecycleEvent(
        TaskQueueLifecycleEventKind kind,
        QueueEnvelope envelope,
        TaskQueueOwnershipToken? ownershipToken = null,
        Error? error = null,
        DateTimeOffset? occurrence = null,
        DateTimeOffset? leaseExpiration = null,
        TaskQueueLifecycleEventMetadata flags = TaskQueueLifecycleEventMetadata.None)
    {
        TaskQueueLifecycleDispatcher<T>? dispatcher = Volatile.Read(ref _lifecycleDispatcher);
        if (dispatcher is null)
        {
            return;
        }

        DateTimeOffset timestamp = occurrence ?? _timeProvider.GetUtcNow();
        TaskQueueLifecycleEvent<T> lifecycleEvent = new(
            dispatcher.NextEventId(),
            kind,
            QueueName,
            envelope.SequenceId,
            envelope.Attempt,
            timestamp,
            envelope.EnqueuedAt,
            envelope.Value,
            error,
            ownershipToken ?? envelope.LastOwnershipToken,
            leaseExpiration,
            flags);

        dispatcher.Publish(lifecycleEvent);
    }

    private sealed record QueueEnvelope(
        T Value,
        long SequenceId,
        int Attempt,
        DateTimeOffset EnqueuedAt,
        Error? LastError,
        TaskQueueOwnershipToken? LastOwnershipToken);

    private sealed class LeaseState
    {
        private readonly Lock _sync = new();
        private int _status; // 0 = active, 1 = completed, 2 = failed, 3 = expired

        internal LeaseState(QueueEnvelope envelope, Guid leaseId, DateTimeOffset grantedAt, DateTimeOffset expiration)
        {
            Envelope = envelope;
            GrantedAt = grantedAt;
            Expiration = expiration;
            LastHeartbeat = grantedAt;
            OwnershipToken = new TaskQueueOwnershipToken(envelope.SequenceId, envelope.Attempt, leaseId);
        }

        internal QueueEnvelope Envelope { get; }

        internal DateTimeOffset GrantedAt { get; }

        internal DateTimeOffset Expiration { get; private set; }

        internal DateTimeOffset LastHeartbeat { get; private set; }

        internal TaskQueueOwnershipToken OwnershipToken { get; }

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

    private sealed class TaskQueueLifecycleDispatcher<TPayload>
    {
        private readonly ConcurrentDictionary<long, ITaskQueueLifecycleListener<TPayload>> _listeners = new();
        private long _listenerId;
        private long _eventId;

        internal IDisposable Register(ITaskQueueLifecycleListener<TPayload> listener)
        {
            long id = Interlocked.Increment(ref _listenerId);
            _listeners[id] = listener;
            return new Subscription(this, id);
        }

        internal long NextEventId() => Interlocked.Increment(ref _eventId);

        internal void Publish(in TaskQueueLifecycleEvent<TPayload> lifecycleEvent)
        {
            foreach (ITaskQueueLifecycleListener<TPayload> listener in _listeners.Values)
            {
                try
                {
                    listener.OnEvent(lifecycleEvent);
                }
                catch
                {
                    // Listener failures should not disrupt queue operations.
                }
            }
        }

        private void Unregister(long id) => _listeners.TryRemove(id, out _);

        private sealed class Subscription : IDisposable
        {
            private TaskQueueLifecycleDispatcher<TPayload>? _dispatcher;
            private long _id;

            internal Subscription(TaskQueueLifecycleDispatcher<TPayload> dispatcher, long id)
            {
                _dispatcher = dispatcher;
                _id = id;
            }

            public void Dispose()
            {
                TaskQueueLifecycleDispatcher<TPayload>? dispatcher = Interlocked.Exchange(ref _dispatcher, null);
                if (dispatcher is null)
                {
                    return;
                }

                dispatcher.Unregister(_id);
                _id = 0;
            }
        }
    }
}
