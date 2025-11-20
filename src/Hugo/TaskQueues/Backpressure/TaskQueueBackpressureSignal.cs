namespace Hugo.TaskQueues.Backpressure;

/// <summary>
/// Represents a backpressure transition captured by <see cref="TaskQueueBackpressureMonitor{T}"/>.
/// </summary>
/// <param name="IsActive">True when the queue is under backpressure.</param>
/// <param name="PendingCount">The pending depth that triggered the transition.</param>
/// <param name="HighWatermark">Configured high watermark.</param>
/// <param name="LowWatermark">Configured low watermark.</param>
/// <param name="ObservedAt">Timestamp associated with the transition.</param>
public readonly record struct TaskQueueBackpressureSignal(
    bool IsActive,
    long PendingCount,
    long HighWatermark,
    long LowWatermark,
    DateTimeOffset ObservedAt);
