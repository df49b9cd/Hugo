using System.Threading;
using System.Threading.RateLimiting;

namespace Hugo.TaskQueues.Backpressure;

/// <summary>
/// Delegate used by rate limiting middleware to select the active <see cref="RateLimiter"/>.
/// </summary>
/// <returns>The limiter currently in effect.</returns>
public delegate RateLimiter TaskQueueLimiterSelector();

/// <summary>
/// Switches between two <see cref="RateLimiter"/> instances when backpressure is enabled or cleared.
/// </summary>
public sealed class BackpressureAwareRateLimiter : ITaskQueueBackpressureListener, IDisposable
{
    private readonly RateLimiter _unthrottled;
    private readonly RateLimiter _backpressure;
    private readonly bool _disposeUnthrottled;
    private readonly bool _disposeBackpressure;
    private RateLimiter _current;

    /// <summary>
    /// Initializes a new instance of the <see cref="BackpressureAwareRateLimiter"/> class.
    /// </summary>
    /// <param name="unthrottledLimiter">Limiter used when the queue is healthy.</param>
    /// <param name="backpressureLimiter">Limiter used once backpressure activates.</param>
    /// <param name="disposeUnthrottledLimiter">True to dispose the unthrottled limiter when this instance is disposed.</param>
    /// <param name="disposeBackpressureLimiter">True to dispose the backpressure limiter when this instance is disposed.</param>
    public BackpressureAwareRateLimiter(
        RateLimiter unthrottledLimiter,
        RateLimiter backpressureLimiter,
        bool disposeUnthrottledLimiter = false,
        bool disposeBackpressureLimiter = false)
    {
        _unthrottled = unthrottledLimiter ?? throw new ArgumentNullException(nameof(unthrottledLimiter));
        _backpressure = backpressureLimiter ?? throw new ArgumentNullException(nameof(backpressureLimiter));
        _disposeUnthrottled = disposeUnthrottledLimiter;
        _disposeBackpressure = disposeBackpressureLimiter;
        _current = _unthrottled;
        LimiterSelector = SelectLimiter;
    }

    /// <summary>
    /// Gets a delegate that resolves the currently active limiter.
    /// </summary>
    public TaskQueueLimiterSelector LimiterSelector { get; }

    /// <summary>
    /// Gets the limiter currently in effect.
    /// </summary>
    public RateLimiter CurrentLimiter => Volatile.Read(ref _current);

    /// <inheritdoc />
    public ValueTask OnSignalAsync(TaskQueueBackpressureSignal signal, CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        Volatile.Write(ref _current, signal.IsActive ? _backpressure : _unthrottled);
        return ValueTask.CompletedTask;
    }

    private RateLimiter SelectLimiter() => Volatile.Read(ref _current);

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposeUnthrottled)
        {
            _unthrottled.Dispose();
        }

        if (_disposeBackpressure)
        {
            _backpressure.Dispose();
        }
    }
}
