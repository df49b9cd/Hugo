using System.Diagnostics.Metrics;

namespace Hugo;

/// <summary>
/// Provides optional instrumentation hooks for Go-inspired primitives.
/// </summary>
public static class GoDiagnostics
{
    private const string DefaultMeterName = "Hugo.Go";

    private static readonly object Sync = new();

    private static Meter? _meter;
    private static Counter<long>? _waitGroupAdditions;
    private static Counter<long>? _waitGroupCompletions;
    private static Counter<long>? _resultSuccesses;
    private static Counter<long>? _resultFailures;
    private static UpDownCounter<long>? _waitGroupOutstanding;
    private static Counter<long>? _channelSelectAttempts;
    private static Counter<long>? _channelSelectCompletions;
    private static Counter<long>? _channelSelectTimeouts;
    private static Counter<long>? _channelSelectCancellations;
    private static Histogram<double>? _channelSelectLatency;
    private static Histogram<long>? _channelDepth;

    /// <summary>
    /// Configures instrumentation using the supplied <see cref="IMeterFactory"/>.
    /// </summary>
    public static void Configure(IMeterFactory factory, string? meterName = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var options = new MeterOptions(meterName ?? DefaultMeterName);
        Configure(factory.Create(options));
    }

    /// <summary>
    /// Configures instrumentation using the provided <see cref="Meter"/> instance.
    /// </summary>
    public static void Configure(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);

        lock (Sync)
        {
            _meter = meter;
            _waitGroupAdditions = meter.CreateCounter<long>("waitgroup.additions", unit: "operations", description: "Number of WaitGroup.Add invocations.");
            _waitGroupCompletions = meter.CreateCounter<long>("waitgroup.completions", unit: "operations", description: "Number of WaitGroup.Done invocations.");
            _resultSuccesses = meter.CreateCounter<long>("result.successes", unit: "events", description: "Number of successful Result instances created.");
            _resultFailures = meter.CreateCounter<long>("result.failures", unit: "events", description: "Number of failed Result instances created.");
            _waitGroupOutstanding = meter.CreateUpDownCounter<long>("waitgroup.outstanding", unit: "tasks", description: "Current number of outstanding WaitGroup operations.");
            _channelSelectAttempts = meter.CreateCounter<long>("channel.select.attempts", unit: "operations", description: "Number of Go.Select attempts.");
            _channelSelectCompletions = meter.CreateCounter<long>("channel.select.completions", unit: "operations", description: "Number of Go.Select operations that completed with a value.");
            _channelSelectTimeouts = meter.CreateCounter<long>("channel.select.timeouts", unit: "operations", description: "Number of Go.Select operations that timed out.");
            _channelSelectCancellations = meter.CreateCounter<long>("channel.select.cancellations", unit: "operations", description: "Number of Go.Select operations that were canceled.");
            _channelSelectLatency = meter.CreateHistogram<double>("channel.select.latency", unit: "ms", description: "Latency of Go.Select operations.");
            _channelDepth = meter.CreateHistogram<long>("channel.depth", unit: "items", description: "Observed depth of channels when values are consumed.");
        }
    }

    /// <summary>
    /// Resets instrumentation to a no-op state. Intended for unit testing.
    /// </summary>
    public static void Reset()
    {
        lock (Sync)
        {
            _meter?.Dispose();
            _meter = null;
            _waitGroupAdditions = null;
            _waitGroupCompletions = null;
            _resultSuccesses = null;
            _resultFailures = null;
            _waitGroupOutstanding = null;
            _channelSelectAttempts = null;
            _channelSelectCompletions = null;
            _channelSelectTimeouts = null;
            _channelSelectCancellations = null;
            _channelSelectLatency = null;
            _channelDepth = null;
        }
    }

    internal static void RecordWaitGroupAdd(int delta)
    {
        if (delta <= 0)
        {
            return;
        }

        _waitGroupAdditions?.Add(delta);
        _waitGroupOutstanding?.Add(delta);
    }

    internal static void RecordWaitGroupDone()
    {
        _waitGroupCompletions?.Add(1);
        _waitGroupOutstanding?.Add(-1);
    }

    internal static void RecordResultSuccess() => _resultSuccesses?.Add(1);

    internal static void RecordResultFailure() => _resultFailures?.Add(1);

    internal static void RecordChannelSelectAttempt() => _channelSelectAttempts?.Add(1);

    internal static void RecordChannelSelectCompleted(TimeSpan duration)
    {
        _channelSelectCompletions?.Add(1);
        _channelSelectLatency?.Record(duration.TotalMilliseconds);
    }

    internal static void RecordChannelSelectTimeout(TimeSpan duration)
    {
        _channelSelectTimeouts?.Add(1);
        _channelSelectLatency?.Record(duration.TotalMilliseconds);
    }

    internal static void RecordChannelSelectCanceled(TimeSpan duration)
    {
        _channelSelectCancellations?.Add(1);
        _channelSelectLatency?.Record(duration.TotalMilliseconds);
    }

    internal static void RecordChannelDepth(long depth)
    {
        if (depth < 0)
        {
            return;
        }

        _channelDepth?.Record(depth);
    }
}
