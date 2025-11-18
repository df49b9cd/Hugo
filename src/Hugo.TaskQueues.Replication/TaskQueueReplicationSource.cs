using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Hugo.TaskQueues.Replication;

/// <summary>
/// Emits ordered replication events when a <see cref="TaskQueue{T}"/> mutates.
/// </summary>
/// <typeparam name="T">Payload type handled by the queue.</typeparam>
public sealed class TaskQueueReplicationSource<T> : ITaskQueueLifecycleListener<T>, IAsyncDisposable
{
    private readonly TaskQueue<T> _queue;
    private readonly TaskQueueReplicationSourceOptions<T> _options;
    private readonly TimeProvider _timeProvider;
    private readonly Channel<TaskQueueReplicationEvent<T>> _events;
    private readonly IDisposable _subscription;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<long, ITaskQueueReplicationObserver<T>> _observers = new();
    private int _disposed;
    private long _sequence;
    private long _observerId;

    /// <summary>
    /// Initializes a new <see cref="TaskQueueReplicationSource{T}"/>.
    /// </summary>
    /// <param name="queue">Queue being observed.</param>
    /// <param name="options">Optional configuration.</param>
    public TaskQueueReplicationSource(TaskQueue<T> queue, TaskQueueReplicationSourceOptions<T>? options = null)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _options = options ?? new TaskQueueReplicationSourceOptions<T>();
        _timeProvider = _options.TimeProvider ?? TimeProvider.System;
        _events = Channel.CreateUnbounded<TaskQueueReplicationEvent<T>>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = true,
            SingleReader = false,
            SingleWriter = false
        });

        _subscription = _queue.RegisterLifecycleListener(this);
    }

    /// <summary>
    /// Initializes a new <see cref="TaskQueueReplicationSource{T}"/> from a safe queue wrapper.
    /// </summary>
    /// <param name="safeQueue">Safe queue wrapper.</param>
    /// <param name="options">Optional configuration.</param>
    public TaskQueueReplicationSource(SafeTaskQueueWrapper<T> safeQueue, TaskQueueReplicationSourceOptions<T>? options = null)
        : this((safeQueue ?? throw new ArgumentNullException(nameof(safeQueue))).UnsafeQueue, options)
    {
    }

    /// <summary>
    /// Gets the reader that consumes replication events.
    /// </summary>
    public ChannelReader<TaskQueueReplicationEvent<T>> Reader => _events.Reader;

    /// <summary>
    /// Watches the queue for mutations and yields replication events.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A streaming sequence of replication events.</returns>
    public IAsyncEnumerable<TaskQueueReplicationEvent<T>> ReadEventsAsync(CancellationToken cancellationToken = default) =>
        _events.Reader.ReadAllAsync(cancellationToken);

    /// <summary>
    /// Registers an observer that receives replication events as they are emitted.
    /// </summary>
    /// <param name="observer">Observer to register.</param>
    /// <returns>A disposable used to unregister the observer.</returns>
    public IDisposable RegisterObserver(ITaskQueueReplicationObserver<T> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);

        long id = Interlocked.Increment(ref _observerId);
        _observers[id] = observer;
        return new ObserverSubscription(this, id);
    }

    /// <summary>
    /// Pumps replication events into the provided channel writer.
    /// </summary>
    /// <param name="writer">Destination writer.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async ValueTask PumpToAsync(ChannelWriter<TaskQueueReplicationEvent<T>> writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);

        await foreach (TaskQueueReplicationEvent<T> replicationEvent in ReadEventsAsync(cancellationToken).ConfigureAwait(false))
        {
            await writer.WriteAsync(replicationEvent, cancellationToken).ConfigureAwait(false);
        }
    }

    void ITaskQueueLifecycleListener<T>.OnEvent(in TaskQueueLifecycleEvent<T> lifecycleEvent)
    {
        TaskQueueReplicationEvent<T> replicationEvent = TaskQueueReplicationEvent<T>.Create(ref _sequence, in lifecycleEvent, _options, _timeProvider);
        if (!_events.Writer.TryWrite(replicationEvent))
        {
            return;
        }

        NotifyObservers(replicationEvent);
        GoDiagnostics.RecordTaskQueueReplicationEvent(lifecycleEvent.QueueName, replicationEvent.Kind.ToString());
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _subscription.Dispose();
        _events.Writer.TryComplete();
        _cts.Cancel();
        _cts.Dispose();
        return ValueTask.CompletedTask;
    }

    private void NotifyObservers(TaskQueueReplicationEvent<T> replicationEvent)
    {
        foreach (ITaskQueueReplicationObserver<T> observer in _observers.Values)
        {
            try
            {
                FireAndForget(observer.OnReplicationEventAsync(replicationEvent, _cts.Token));
            }
            catch
            {
            }
        }
    }

    private void UnregisterObserver(long id) => _observers.TryRemove(id, out _);

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

    private sealed class ObserverSubscription : IDisposable
    {
        private TaskQueueReplicationSource<T>? _source;
        private long _observerId;

        internal ObserverSubscription(TaskQueueReplicationSource<T> source, long observerId)
        {
            _source = source;
            _observerId = observerId;
        }

        public void Dispose()
        {
            TaskQueueReplicationSource<T>? source = Interlocked.Exchange(ref _source, null);
            if (source is null)
            {
                return;
            }

            source.UnregisterObserver(_observerId);
            _observerId = 0;
        }
    }
}
