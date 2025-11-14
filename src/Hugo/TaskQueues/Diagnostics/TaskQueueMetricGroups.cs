namespace Hugo.TaskQueues.Diagnostics;

/// <summary>
/// Flags describing the TaskQueue instrumentation groups that emit metrics.
/// </summary>
[Flags]
public enum TaskQueueMetricGroups
{
    /// <summary>No metrics are emitted.</summary>
    None = 0,

    /// <summary>Queue depth histograms and gauges.</summary>
    QueueDepth = 1,

    /// <summary>Throughput counters and lease duration histograms.</summary>
    Throughput = 2,

    /// <summary>Backpressure transition counters and duration histograms.</summary>
    Backpressure = 4,

    /// <summary>Replication event/lag instruments.</summary>
    Replication = 8,

    /// <summary>All TaskQueue metric groups.</summary>
    All = QueueDepth | Throughput | Backpressure | Replication
}
