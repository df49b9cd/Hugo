namespace Hugo;

/// <summary>
/// Well-known error codes emitted by the library.
/// </summary>
public static class ErrorCodes
{
    public const string Unspecified = "error.unspecified";
    public const string Canceled = "error.canceled";
    public const string Timeout = "error.timeout";
    public const string Exception = "error.exception";
    public const string Aggregate = "error.aggregate";
    public const string Validation = "error.validation";
    public const string SelectDrained = "error.select.drained";
    public const string VersionConflict = "error.version.conflict";
    public const string DeterministicReplay = "error.deterministic.replay";
    public const string TaskQueueLeaseExpired = "error.taskqueue.lease_expired";
    public const string TaskQueueAbandoned = "error.taskqueue.abandoned";
    public const string TaskQueueDeadLettered = "error.taskqueue.deadlettered";
    public const string TaskQueueDisposed = "error.taskqueue.disposed";
    public const string TaskQueueLeaseInactive = "error.taskqueue.lease_inactive";
    public const string ChannelCompleted = "error.channel.completed";
}
