using Hugo.TaskQueues.Backpressure;
using Hugo.TaskQueues.Replication;

namespace Hugo.TaskQueues.Diagnostics;

/// <summary>
/// Base record for streamed TaskQueue diagnostics events.
/// </summary>
/// <param name="ObservedAt">Timestamp recorded for the event.</param>
public abstract record class TaskQueueDiagnosticsEvent(DateTimeOffset ObservedAt);

/// <summary>Represents a streamed backpressure signal.</summary>
/// <param name="Signal">Signal payload.</param>
public sealed record class TaskQueueBackpressureDiagnosticsEvent(TaskQueueBackpressureSignal Signal)
    : TaskQueueDiagnosticsEvent(Signal.ObservedAt);

/// <summary>Represents metadata extracted from a replication event for diagnostics endpoints.</summary>
public sealed record class TaskQueueReplicationDiagnosticsEvent(
    string QueueName,
    TaskQueueReplicationEventKind Kind,
    long SequenceNumber,
    long SourceEventId,
    long TaskSequenceId,
    int Attempt,
    DateTimeOffset OccurredAt,
    DateTimeOffset RecordedAt,
    TimeSpan WallClockLag,
    long SequenceDelta,
    TaskQueueLifecycleEventMetadata Flags)
    : TaskQueueDiagnosticsEvent(RecordedAt);
