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
    /// <param name="delay">The delay duration or <see cref="Timeout.InfiniteTimeSpan"/>.</param>
    /// <param name="provider">The optional time provider used to schedule the delay.</param>
    /// <param name="cancellationToken">The token used to cancel the delay.</param>
    /// <returns>A task that completes when the delay elapses.</returns>
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
    /// <param name="period">The interval between ticks.</param>
    /// <param name="provider">The optional time provider used to schedule ticks.</param>
    /// <param name="cancellationToken">The token used to cancel the ticker.</param>
    /// <returns>A ticker that exposes a channel reader for consumption.</returns>
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
    /// <param name="period">The interval between ticks.</param>
    /// <param name="provider">The optional time provider used to schedule ticks.</param>
    /// <param name="cancellationToken">The token used to cancel the ticker.</param>
    /// <returns>A channel reader that delivers tick timestamps.</returns>
    public static ChannelReader<DateTimeOffset> Tick(TimeSpan period, TimeProvider? provider = null, CancellationToken cancellationToken = default) =>
        NewTicker(period, provider, cancellationToken).Reader;

    /// <summary>
    /// Returns a channel that receives the current time once after the provided delay.
    /// </summary>
    /// <param name="delay">The delay before publishing the single value.</param>
    /// <param name="provider">The optional time provider used to schedule the delay.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A channel reader that emits a single timestamp.</returns>
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
    /// <param name="delay">The delay before completing the task.</param>
    /// <param name="provider">The optional time provider used to schedule the delay.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task completing with the timestamp when the delay finished.</returns>
    public static Task<DateTimeOffset> AfterAsync(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ChannelReader<DateTimeOffset> reader = After(delay, provider, cancellationToken);
        return reader.ReadAsync(cancellationToken).AsTask();
    }

    /// <summary>Represents a timer-backed ticker that publishes periodic timestamps.</summary>
    public readonly struct GoTicker : IAsyncDisposable, IDisposable
    {
        private readonly TimerChannel? _channel;

        internal GoTicker(TimerChannel channel)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        private TimerChannel Channel => _channel ?? throw new InvalidOperationException("Ticker is not initialized.");

        /// <summary>Gets the channel reader that surfaces ticker events.</summary>
        public ChannelReader<DateTimeOffset> Reader => Channel.Reader;

        /// <summary>Reads the next available timestamp from the ticker.</summary>
        /// <param name="cancellationToken">The token used to cancel the read.</param>
        /// <returns>The next tick timestamp.</returns>
        public ValueTask<DateTimeOffset> ReadAsync(CancellationToken cancellationToken = default) => Channel.ReadAsync(cancellationToken);

        /// <summary>Attempts to synchronously read the next available timestamp.</summary>
        /// <param name="value">When this method returns, contains the next available timestamp if successful.</param>
        /// <returns><see langword="true"/> if a value was read; otherwise <see langword="false"/>.</returns>
        public bool TryRead(out DateTimeOffset value) => Channel.TryRead(out value);

        /// <summary>Stops the ticker and releases underlying resources.</summary>
        public void Stop() => Channel.Dispose();

        /// <summary>Stops the ticker asynchronously.</summary>
        /// <returns>A task that completes once the ticker has stopped.</returns>
        public ValueTask StopAsync() => Channel.DisposeAsync();

        /// <inheritdoc />
        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync() => StopAsync();
    }
}
