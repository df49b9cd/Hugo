using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading;

namespace Hugo.TaskQueues.Diagnostics;

/// <summary>
/// Represents a disposable registration that wires TaskQueue diagnostics into a host.
/// </summary>
public sealed class TaskQueueDiagnosticsRegistration : IAsyncDisposable
{
    private readonly Meter? _meter;
    private readonly ActivitySource? _activitySource;
    private readonly IDisposable? _samplingSubscription;
    private readonly IDisposable? _tagSubscription;
    private readonly IDisposable? _metricGroupSubscription;
    private readonly bool _resetOnDispose;
    private int _disposed;

    private TaskQueueDiagnosticsRegistration(
        Meter? meter,
        ActivitySource? activitySource,
        IDisposable? samplingSubscription,
        IDisposable? tagSubscription,
        IDisposable? metricGroupSubscription,
        bool resetOnDispose)
    {
        _meter = meter;
        _activitySource = activitySource;
        _samplingSubscription = samplingSubscription;
        _tagSubscription = tagSubscription;
        _metricGroupSubscription = metricGroupSubscription;
        _resetOnDispose = resetOnDispose;
    }

    internal static TaskQueueDiagnosticsRegistration Create(IMeterFactory meterFactory, TaskQueueDiagnosticsOptions options)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);
        ArgumentNullException.ThrowIfNull(options);

        Meter? meter = null;
        if (options.ConfigureMeter)
        {
            meter = GoDiagnostics.CreateMeter(meterFactory, options.MeterName);
            GoDiagnostics.Configure(meter);
        }

        ActivitySource? activitySource = null;
        IDisposable? samplingSubscription = null;
        if (options.Activity.ConfigureActivitySource)
        {
            activitySource = GoDiagnostics.CreateActivitySource(
                options.Activity.Name,
                options.Activity.Version,
                options.Activity.SchemaUrl);

            GoDiagnostics.Configure(activitySource);

            if (options.Activity.EnableRateLimitedSampling && activitySource is not null)
            {
                int maxActivities = Math.Max(1, options.Activity.MaxActivitiesPerInterval);
                TimeSpan interval = options.Activity.SamplingInterval <= TimeSpan.Zero
                    ? TimeSpan.FromSeconds(1)
                    : options.Activity.SamplingInterval;

                samplingSubscription = GoDiagnostics.UseRateLimitedSampling(
                    activitySource,
                    maxActivities,
                    interval,
                    options.Activity.UnsampledResult);
            }
        }

        TaskQueueTagEnricher? enricher = BuildTagEnricher(options.Metrics);
        IDisposable? tagSubscription = enricher is null ? null : GoDiagnostics.RegisterTaskQueueTagEnricher(enricher);
        IDisposable metricGroupSubscription = GoDiagnostics.ConfigureTaskQueueMetrics(options.Metrics.EnabledGroups);

        return new TaskQueueDiagnosticsRegistration(
            meter,
            activitySource,
            samplingSubscription,
            tagSubscription,
            metricGroupSubscription,
            options.ResetOnDispose);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        _samplingSubscription?.Dispose();
        _tagSubscription?.Dispose();
        _metricGroupSubscription?.Dispose();

        if (_resetOnDispose)
        {
            GoDiagnostics.Reset();
            return ValueTask.CompletedTask;
        }

        _activitySource?.Dispose();
        _meter?.Dispose();
        return ValueTask.CompletedTask;
    }

    private static TaskQueueTagEnricher? BuildTagEnricher(TaskQueueMetricsOptions metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);

        bool emitsTags =
            !string.IsNullOrWhiteSpace(metrics.ServiceName) ||
            !string.IsNullOrWhiteSpace(metrics.DefaultShard) ||
            metrics.ShardResolver is not null ||
            metrics.DefaultTags.Count > 0 ||
            metrics.TagEnrichers.Count > 0;

        if (!emitsTags)
        {
            return null;
        }

        return (in TaskQueueTagContext context, ref TagList tags) =>
        {
            if (!string.IsNullOrWhiteSpace(metrics.ServiceName))
            {
                tags.Add("service.name", metrics.ServiceName!);
            }

            string? shard = metrics.ShardResolver?.Invoke(context.QueueName) ?? metrics.DefaultShard;
            if (!string.IsNullOrWhiteSpace(shard))
            {
                tags.Add("taskqueue.shard", shard!);
            }

            foreach (KeyValuePair<string, object?> tag in metrics.DefaultTags)
            {
                tags.Add(tag.Key, tag.Value ?? string.Empty);
            }

            foreach (TaskQueueTagEnricher enricher in metrics.TagEnrichers)
            {
                enricher(in context, ref tags);
            }
        };
    }
}
