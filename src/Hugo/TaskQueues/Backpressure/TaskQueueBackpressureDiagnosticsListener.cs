using System.Threading;
using System.Threading.Channels;

namespace Hugo.TaskQueues.Backpressure;

/// <summary>
/// Captures backpressure signals in a bounded channel for diagnostics endpoints.
/// </summary>
public sealed class TaskQueueBackpressureDiagnosticsListener : ITaskQueueBackpressureListener, IAsyncDisposable
{
    private readonly Channel<TaskQueueBackpressureSignal> _channel;
    private TaskQueueBackpressureSignal _latest = new(false, 0, 0, 0, DateTimeOffset.MinValue);

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskQueueBackpressureDiagnosticsListener"/> class.
    /// </summary>
    /// <param name="capacity">Maximum number of retained signals.</param>
    public TaskQueueBackpressureDiagnosticsListener(int capacity = 64)
    {
        if (capacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be at least one.");
        }

        var options = new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = true
        };

        _channel = Channel.CreateBounded<TaskQueueBackpressureSignal>(options);
    }

    /// <summary>
    /// Gets a reader that streams the captured signals.
    /// </summary>
    public ChannelReader<TaskQueueBackpressureSignal> Reader => _channel.Reader;

    /// <summary>
    /// Gets the latest signal observed by the listener.
    /// </summary>
    public TaskQueueBackpressureSignal Latest => Volatile.Read(ref _latest);

    /// <inheritdoc />
    public ValueTask OnSignalAsync(TaskQueueBackpressureSignal signal, CancellationToken cancellationToken = default)
    {
        Volatile.Write(ref _latest, signal);
        return _channel.Writer.WriteAsync(signal, cancellationToken);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
