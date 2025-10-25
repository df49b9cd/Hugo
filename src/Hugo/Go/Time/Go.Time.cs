using System.Threading.Channels;

namespace Hugo;

/// <content>
/// Provides time-based helpers such as delays, tickers, and timers.
/// </content>
public static partial class Go
{
    /// <summary>
    /// Creates a cancellable delay that honors the supplied <see cref="TimeProvider"/> when provided.
    /// </summary>
    public static Task DelayAsync(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        provider ??= TimeProvider.System;
        return provider.DelayAsync(delay, cancellationToken);
    }

    /// <summary>
    /// Creates a ticker that delivers the current time on its channel at the specified period.
    /// </summary>
    public static GoTicker NewTicker(TimeSpan period, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "Ticker period must be positive.");
        }

        provider ??= TimeProvider.System;
        TimerChannel timerChannel = TimerChannel.Start(provider, period, period, cancellationToken, singleShot: false);
        return new GoTicker(timerChannel);
    }

    /// <summary>
    /// Returns a channel that delivers periodic ticks at the specified period.
    /// </summary>
    public static ChannelReader<DateTimeOffset> Tick(TimeSpan period, TimeProvider? provider = null, CancellationToken cancellationToken = default) =>
        NewTicker(period, provider, cancellationToken).Reader;

    /// <summary>
    /// Returns a channel that receives the current time once after the provided delay.
    /// </summary>
    public static ChannelReader<DateTimeOffset> After(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        if (delay == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay cannot be infinite.");
        }

        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must be non-negative.");
        }

        provider ??= TimeProvider.System;
        TimerChannel timerChannel = TimerChannel.Start(provider, delay, Timeout.InfiniteTimeSpan, cancellationToken, singleShot: true);
        return timerChannel.Reader;
    }

    /// <summary>
    /// Returns a task that completes once the provided delay has elapsed.
    /// </summary>
    public static Task<DateTimeOffset> AfterAsync(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ChannelReader<DateTimeOffset> reader = After(delay, provider, cancellationToken);
        return reader.ReadAsync(cancellationToken).AsTask();
    }

    public readonly struct GoTicker : IAsyncDisposable, IDisposable
    {
        private readonly TimerChannel? _channel;

        internal GoTicker(TimerChannel channel)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        private TimerChannel Channel => _channel ?? throw new InvalidOperationException("Ticker is not initialized.");

        public ChannelReader<DateTimeOffset> Reader => Channel.Reader;

        public ValueTask<DateTimeOffset> ReadAsync(CancellationToken cancellationToken = default) => Channel.ReadAsync(cancellationToken);

        public bool TryRead(out DateTimeOffset value) => Channel.TryRead(out value);

        public void Stop() => Channel.Dispose();

        public ValueTask StopAsync() => Channel.DisposeAsync();

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        public ValueTask DisposeAsync() => StopAsync();
    }
}
