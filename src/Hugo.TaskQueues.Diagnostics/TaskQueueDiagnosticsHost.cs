using System.Diagnostics.Metrics;
using System.Threading.Channels;

using Hugo.TaskQueues.Backpressure;
using Hugo.TaskQueues.Replication;

namespace Hugo.TaskQueues.Diagnostics;

/// <summary>
/// Hosts TaskQueue diagnostics listeners and exposes a unified stream for control-plane endpoints.
/// </summary>
public sealed class TaskQueueDiagnosticsHost : IAsyncDisposable
{
    private readonly TaskQueueDiagnosticsRegistration _registration;
    private readonly Channel<TaskQueueDiagnosticsEvent> _events;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _subscriptions = [];
    private readonly List<Task> _pumps = [];
    private readonly Dictionary<string, long> _replicationSequences = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private int _disposed;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskQueueDiagnosticsHost"/> class.
    /// </summary>
    /// <param name="meterFactory">Meter factory used to configure diagnostics.</param>
    /// <param name="configure">Optional configuration delegate.</param>
    /// <param name="backpressureCapacity">Maximum number of cached backpressure signals.</param>
    public TaskQueueDiagnosticsHost(IMeterFactory meterFactory, Action<TaskQueueDiagnosticsOptions>? configure = null, int backpressureCapacity = 256)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        if (backpressureCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(backpressureCapacity), backpressureCapacity, "Capacity must be positive.");
        }

        _registration = meterFactory.AddTaskQueueDiagnostics(configure);
        BackpressureListener = new TaskQueueBackpressureDiagnosticsListener(backpressureCapacity);
        _events = Channel.CreateUnbounded<TaskQueueDiagnosticsEvent>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false
        });

        _pumps.Add(Task.Run(() => PumpBackpressureAsync(_cts.Token)));
    }

    /// <summary>Gets the listener streaming backpressure signals.</summary>
    public TaskQueueBackpressureDiagnosticsListener BackpressureListener { get; }

    /// <summary>Gets the unified diagnostics stream.</summary>
    public ChannelReader<TaskQueueDiagnosticsEvent> Events => _events.Reader;

    /// <summary>
    /// Attaches a <see cref="TaskQueueBackpressureMonitor{T}"/> so its signals flow to the host.
    /// </summary>
    /// <typeparam name="T">Work item type.</typeparam>
    /// <param name="monitor">Monitor to attach.</param>
    /// <returns>A disposable handle used to detach the monitor.</returns>
    public IDisposable Attach<T>(TaskQueueBackpressureMonitor<T> monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        var subscription = monitor.RegisterListener(BackpressureListener);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    /// <summary>
    /// Attaches a <see cref="TaskQueueReplicationSource{T}"/> so replication events are included in the diagnostics stream.
    /// </summary>
    /// <typeparam name="T">Work item type.</typeparam>
    /// <param name="source">Replication source to observe.</param>
    /// <returns>A disposable handle used to detach the observer.</returns>
    public IDisposable Attach<T>(TaskQueueReplicationSource<T> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var observer = new TaskQueueReplicationDiagnosticsObserver<T>(this);
        var subscription = source.RegisterObserver(observer);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
        }

        return subscription;
    }

    internal ValueTask PublishBackpressureAsync(TaskQueueBackpressureSignal signal, CancellationToken cancellationToken) => _events.Writer.WriteAsync(new TaskQueueBackpressureDiagnosticsEvent(signal), cancellationToken);

    internal ValueTask PublishReplicationAsync<T>(TaskQueueReplicationEvent<T> replicationEvent, CancellationToken cancellationToken)
    {
        long delta = TrackSequenceDelta(replicationEvent.QueueName, replicationEvent.SequenceNumber);
        var wallClockLag = replicationEvent.RecordedAt - replicationEvent.OccurredAt;

        var envelope = new TaskQueueReplicationDiagnosticsEvent(
            replicationEvent.QueueName,
            replicationEvent.Kind,
            replicationEvent.SequenceNumber,
            replicationEvent.SourceEventId,
            replicationEvent.TaskSequenceId,
            replicationEvent.Attempt,
            replicationEvent.OccurredAt,
            replicationEvent.RecordedAt,
            wallClockLag,
            delta,
            replicationEvent.Flags);

        return _events.Writer.WriteAsync(envelope, cancellationToken);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync();
        _events.Writer.TryComplete();

        lock (_gate)
        {
            foreach (IDisposable subscription in _subscriptions)
            {
                subscription.Dispose();
            }

            _subscriptions.Clear();
        }

        try
        {
            await Task.WhenAll(_pumps).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        await BackpressureListener.DisposeAsync().ConfigureAwait(false);
        await _registration.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task PumpBackpressureAsync(CancellationToken cancellationToken)
    {
        await foreach (TaskQueueBackpressureSignal signal in BackpressureListener.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            try
            {
                await PublishBackpressureAsync(signal, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
            }
        }
    }

    private long TrackSequenceDelta(string queueName, long sequenceNumber)
    {
        lock (_replicationSequences)
        {
            if (_replicationSequences.TryGetValue(queueName, out long previous))
            {
                _replicationSequences[queueName] = sequenceNumber;
                return Math.Max(sequenceNumber - previous, 0);
            }

            _replicationSequences[queueName] = sequenceNumber;
            return 1;
        }
    }

    private sealed class TaskQueueReplicationDiagnosticsObserver<T>(TaskQueueDiagnosticsHost host) : ITaskQueueReplicationObserver<T>
    {
        private readonly TaskQueueDiagnosticsHost _host = host ?? throw new ArgumentNullException(nameof(host));

        public ValueTask OnReplicationEventAsync(TaskQueueReplicationEvent<T> replicationEvent, CancellationToken cancellationToken = default) =>
            _host.PublishReplicationAsync(replicationEvent, cancellationToken);
    }
}
