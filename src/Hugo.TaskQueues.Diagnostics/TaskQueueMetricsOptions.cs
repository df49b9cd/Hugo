using System.Collections.Generic;

namespace Hugo.TaskQueues.Diagnostics;

/// <summary>
/// Configures TaskQueue metric emission, tagging, and histogram recommendations.
/// </summary>
public sealed class TaskQueueMetricsOptions
{
    /// <summary>Gets or sets the enabled metric groups.</summary>
    public TaskQueueMetricGroups EnabledGroups { get; set; } = TaskQueueMetricGroups.All;

    /// <summary>Gets or sets the service name tag value applied to every measurement.</summary>
    public string? ServiceName { get; set; }

    /// <summary>Gets or sets the default shard identifier attached to metrics.</summary>
    public string? DefaultShard { get; set; }

    /// <summary>Gets or sets a resolver that maps queue names to shard identifiers.</summary>
    public Func<string?, string?>? ShardResolver { get; set; }

    /// <summary>Gets the static tag set applied to every measurement.</summary>
    public IDictionary<string, object?> DefaultTags { get; } = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>Gets the custom tag enrichers invoked after the built-in service/shard tags are applied.</summary>
    public IList<TaskQueueTagEnricher> TagEnrichers { get; } = [];

    /// <summary>Recommended histogram buckets (items) for pending depth charts.</summary>
    public IList<double> PendingDepthBuckets { get; } = [16, 32, 64, 128, 256, 512, 1024, 4096];

    /// <summary>Recommended histogram buckets (leases) for active worker charts.</summary>
    public IList<double> ActiveLeaseBuckets { get; } = [4, 8, 16, 32, 64, 128, 256];

    /// <summary>Recommended histogram buckets (ms) for backpressure duration charts.</summary>
    public IList<double> BackpressureDurationBuckets { get; } = [10, 50, 100, 250, 500, 1_000, 5_000, 15_000];

    /// <summary>Recommended histogram buckets (ms) for replication lag charts.</summary>
    public IList<double> ReplicationLagBuckets { get; } = [1, 5, 10, 25, 50, 100, 250, 500];
}
