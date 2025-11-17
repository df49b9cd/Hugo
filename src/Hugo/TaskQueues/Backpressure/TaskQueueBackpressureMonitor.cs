using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Hugo.TaskQueues.Backpressure;

/// <summary>
/// Provides reusable backpressure monitoring for <see cref="TaskQueue{T}"/> and <see cref="SafeTaskQueueWrapper{T}"/>.
/// </summary>
/// <typeparam name="T">Work item type.</typeparam>
public sealed class TaskQueueBackpressureMonitor<T> : IAsyncDisposable
{
    private readonly TaskQueue<T> _queue;
    private readonly long _highWatermark;
    private readonly long _lowWatermark;
    private readonly TimeSpan _cooldown;
    private readonly Channel<TaskQueueBackpressureSignal> _signals;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, ITaskQueueBackpressureListener> _listeners = new();
    private readonly Lock _waiterLock = new();
    private List<TaskCompletionSource<TaskQueueBackpressureSignal>>? _waiters;
    private TaskQueueBackpressureSignal _currentSignal;
    private DateTimeOffset _stateStartedAt;
    private int _isBackpressureActive;
    private int _disposed;
    private long _listenerId;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskQueueBackpressureMonitor{T}"/> class.
    /// </summary>
    /// <param name="queue">The queue being monitored.</param>
    /// <param name="options">Monitor configuration.</param>
    public TaskQueueBackpressureMonitor(TaskQueue<T> queue, TaskQueueBackpressureMonitorOptions options)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        ArgumentNullException.ThrowIfNull(options);

        _highWatermark = options.HighWatermark;
        _lowWatermark = ResolveLowWatermark(options.HighWatermark, options.LowWatermark);
        _cooldown = options.Cooldown;

        _signals = Channel.CreateUnbounded<TaskQueueBackpressureSignal>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true,
            AllowSynchronousContinuations = false
        });

        _stateStartedAt = DateTimeOffset.UtcNow;
        _currentSignal = new TaskQueueBackpressureSignal(false, _queue.PendingCount, _highWatermark, _lowWatermark, _stateStartedAt);
        ApplyQueueConfiguration();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskQueueBackpressureMonitor{T}"/> class using a safe queue wrapper.
    /// </summary>
    /// <param name="safeQueue">Safe queue wrapper.</param>
    /// <param name="options">Monitor configuration.</param>
    public TaskQueueBackpressureMonitor(SafeTaskQueueWrapper<T> safeQueue, TaskQueueBackpressureMonitorOptions options)
        : this(safeQueue?.UnsafeQueue ?? throw new ArgumentNullException(nameof(safeQueue)), options)
    {
    }

    /// <summary>
    /// Gets the latest signal that was processed.
    /// </summary>
    public TaskQueueBackpressureSignal CurrentSignal => Volatile.Read(ref _currentSignal)!;

    /// <summary>
    /// Gets a value indicating whether backpressure is currently active.
    /// </summary>
    public bool IsActive => Volatile.Read(ref _isBackpressureActive) == 1;

    /// <summary>
    /// Registers a listener that will be invoked for each signal.
    /// </summary>
    /// <param name="listener">Listener to register.</param>
    /// <returns>An <see cref="IDisposable"/> that unregisters the listener.</returns>
    public IDisposable RegisterListener(ITaskQueueBackpressureListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        long id = Interlocked.Increment(ref _listenerId);
        _listeners[id] = listener;

        FireAndForget(listener.OnSignalAsync(CurrentSignal, _cts.Token));
        return new ListenerSubscription(this, id);
    }

    /// <summary>
    /// Waits until backpressure clears.
    /// </summary>
    /// <param name="cancellationToken">Token used to cancel the wait.</param>
    /// <returns>The signal that cleared the backpressure condition.</returns>
    public ValueTask<TaskQueueBackpressureSignal> WaitForDrainingAsync(CancellationToken cancellationToken = default)
    {
        TaskQueueBackpressureSignal snapshot = CurrentSignal;
        if (!snapshot.IsActive)
        {
            return new ValueTask<TaskQueueBackpressureSignal>(snapshot);
        }

        var source = new TaskCompletionSource<TaskQueueBackpressureSignal>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;

        lock (_waiterLock)
        {
            snapshot = CurrentSignal;
            if (!snapshot.IsActive)
            {
                return new ValueTask<TaskQueueBackpressureSignal>(snapshot);
            }

            (_waiters ??= []).Add(source);

            if (cancellationToken.CanBeCanceled)
            {
                registration = cancellationToken.Register(static state =>
                {
                    var (monitor, waiter, token) = ((TaskQueueBackpressureMonitor<T>, TaskCompletionSource<TaskQueueBackpressureSignal>, CancellationToken))state!;
                    monitor.CancelWaiter(waiter, token);
                }, (this, source, cancellationToken));
            }
        }

        return new ValueTask<TaskQueueBackpressureSignal>(AwaitWaiterAsync(source.Task, registration));
    }

    private static async Task<TaskQueueBackpressureSignal> AwaitWaiterAsync(Task<TaskQueueBackpressureSignal> task, CancellationTokenRegistration registration)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        finally
        {
            await registration.DisposeAsync();
        }
    }

    private void CancelWaiter(TaskCompletionSource<TaskQueueBackpressureSignal> source, CancellationToken token)
    {
        if (!source.TrySetCanceled(token))
        {
            return;
        }

        lock (_waiterLock)
        {
            _waiters?.Remove(source);
        }
    }

    private void CompleteWaiters(TaskQueueBackpressureSignal signal)
    {
        List<TaskCompletionSource<TaskQueueBackpressureSignal>>? waiters = null;

        lock (_waiterLock)
        {
            if (_waiters is null || _waiters.Count == 0)
            {
                return;
            }

            waiters = _waiters;
            _waiters = null;
        }

        foreach (TaskCompletionSource<TaskQueueBackpressureSignal> waiter in waiters)
        {
            waiter.TrySetResult(signal);
        }
    }

    private async ValueTask PumpSignalsAsync(CancellationToken cancellationToken)
    {
        ChannelReader<TaskQueueBackpressureSignal> reader = _signals.Reader;

        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out TaskQueueBackpressureSignal? signal))
                {
                    await DispatchSignalAsync(signal, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask DispatchSignalAsync(TaskQueueBackpressureSignal signal, CancellationToken cancellationToken)
    {
        TimeSpan elapsed = signal.ObservedAt - _stateStartedAt;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        _stateStartedAt = signal.ObservedAt;
        Volatile.Write(ref _currentSignal, signal);
        Volatile.Write(ref _isBackpressureActive, signal.IsActive ? 1 : 0);

        if (!signal.IsActive)
        {
            CompleteWaiters(signal);
        }

        foreach (ITaskQueueBackpressureListener listener in _listeners.Values)
        {
            try
            {
                await listener.OnSignalAsync(signal, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Listener failures are isolated to avoid cascading disruptions.
            }
        }

        GoDiagnostics.RecordTaskQueueBackpressureSignal(_queue.QueueName, signal, elapsed);
    }

    private void ApplyQueueConfiguration()
    {
        TaskQueueBackpressureOptions options = new()
        {
            HighWatermark = _highWatermark,
            LowWatermark = _lowWatermark,
            Cooldown = _cooldown,
            StateChanged = HandleQueueSignal
        };

        _queue.ConfigureBackpressure(options);
    }

    private void HandleQueueSignal(TaskQueueBackpressureState state)
    {
        var signal = new TaskQueueBackpressureSignal(state.IsActive, state.PendingCount, _highWatermark, _lowWatermark, state.ObservedAt);
        _signals.Writer.TryWrite(signal);
    }

    private static long ResolveLowWatermark(long high, long configuredLow) =>
        configuredLow <= 0 || configuredLow >= high ? Math.Max(1, high / 2) : configuredLow;

    private void UnregisterListener(long id) => _listeners.TryRemove(id, out _);

    private sealed class ListenerSubscription : IDisposable
    {
        private TaskQueueBackpressureMonitor<T>? _monitor;
        private long _listenerId;

        internal ListenerSubscription(TaskQueueBackpressureMonitor<T> monitor, long listenerId)
        {
            _monitor = monitor;
            _listenerId = listenerId;
        }

        public void Dispose()
        {
            TaskQueueBackpressureMonitor<T>? monitor = Interlocked.Exchange(ref _monitor, null);
            if (monitor is null)
            {
                return;
            }

            monitor.UnregisterListener(_listenerId);
            _listenerId = 0;
        }
    }

    private static void FireAndForget(ValueTask task)
    {
        if (task.IsCompletedSuccessfully)
        {
            return;
        }

        _ = AwaitAsync(task);
    }

    private static async Task AwaitAsync(ValueTask task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.ConfigureBackpressure(null);
        _signals.Writer.TryComplete();
        await _cts.CancelAsync();

        try
        {
            await PumpSignalsAsync(_cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
        }

        _cts.Dispose();

        lock (_waiterLock)
        {
            if (_waiters is null)
            {
                return;
            }

            foreach (TaskCompletionSource<TaskQueueBackpressureSignal> waiter in _waiters)
            {
                waiter.TrySetException(new ObjectDisposedException(nameof(TaskQueueBackpressureMonitor<T>)));
            }

            _waiters.Clear();
        }
    }
}
