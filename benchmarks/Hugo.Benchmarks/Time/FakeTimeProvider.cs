using System.Diagnostics;

namespace Hugo.Benchmarks.Time;

/// <summary>
/// Minimal deterministic time provider for benchmarks; advances only when requested.
/// </summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public void Advance(TimeSpan delta)
    {
        if (delta < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delta));
        }

        _utcNow = _utcNow.Add(delta);
    }

    public override long TimestampFrequency => Stopwatch.Frequency;

    public override long GetTimestamp() => _stopwatch.ElapsedTicks;
}
