using System.Threading.Channels;

using Hugo.TaskQueues;

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
    private int _disposed;
    private long _sequence;

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
        return ValueTask.CompletedTask;
    }
}
