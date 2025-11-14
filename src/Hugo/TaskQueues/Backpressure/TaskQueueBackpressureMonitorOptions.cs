namespace Hugo.TaskQueues.Backpressure;

/// <summary>
/// Configures <see cref="TaskQueueBackpressureMonitor{T}"/> instances.
/// </summary>
public sealed class TaskQueueBackpressureMonitorOptions
{
    private long _highWatermark = 512;
    private long _lowWatermark = -1;
    private TimeSpan _cooldown = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the pending depth that activates backpressure monitoring. Defaults to 512 items.
    /// </summary>
    public long HighWatermark
    {
        get => _highWatermark;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "High watermark must be greater than zero.");
            }

            _highWatermark = value;
        }
    }

    /// <summary>
    /// Gets or sets the pending depth that clears backpressure. Defaults to <see cref="HighWatermark"/> / 2.
    /// </summary>
    public long LowWatermark
    {
        get => _lowWatermark;
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
    /// Gets or sets the minimum duration between repeated transitions. Defaults to 5 seconds.
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
}
