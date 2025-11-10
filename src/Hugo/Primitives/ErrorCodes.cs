using System.Collections.Generic;

namespace Hugo;

/// <summary>
/// Provides well-known error codes emitted by the library together with descriptive metadata.
/// </summary>
public static class ErrorCodes
{
    /// <summary>Aggregation of one or more nested errors surfaced by a result pipeline.</summary>
    public const string Aggregate = "error.aggregate";

    /// <summary>Operation canceled by a linked token or cancellation-aware pipeline.</summary>
    public const string Canceled = "error.canceled";

    /// <summary>Channel completed before a pending operation could finish.</summary>
    public const string ChannelCompleted = "error.channel.completed";

    /// <summary>Deterministic gate replay attempted to re-execute a captured side effect.</summary>
    public const string DeterministicReplay = "error.deterministic.replay";

    /// <summary>Unhandled exception surfaced by a result pipeline.</summary>
    public const string Exception = "error.exception";

    /// <summary>All channel cases completed without yielding a value while selecting.</summary>
    public const string SelectDrained = "error.select.drained";

    /// <summary>Task queue operation abandoned after exceeding the retry policy.</summary>
    public const string TaskQueueAbandoned = "error.taskqueue.abandoned";

    /// <summary>Task queue work item moved to the dead-letter handler.</summary>
    public const string TaskQueueDeadLettered = "error.taskqueue.deadlettered";

    /// <summary>Task queue operations attempted after disposal.</summary>
    public const string TaskQueueDisposed = "error.taskqueue.disposed";

    /// <summary>Lease expired without being completed or extended.</summary>
    public const string TaskQueueLeaseExpired = "error.taskqueue.lease_expired";

    /// <summary>Lease is no longer active and cannot be manipulated.</summary>
    public const string TaskQueueLeaseInactive = "error.taskqueue.lease_inactive";

    /// <summary>Operation exceeded the allotted deadline.</summary>
    public const string Timeout = "error.timeout";

    /// <summary>Fallback error used when the failure category is unknown.</summary>
    public const string Unspecified = "error.unspecified";

    /// <summary>Input validation failed or precondition was not satisfied.</summary>
    public const string Validation = "error.validation";

    /// <summary>Deterministic workflow attempted to execute an unsupported version.</summary>
    public const string VersionConflict = "error.version.conflict";

    private static readonly ErrorDescriptor[] AllDescriptors =
    [
        new(nameof(Aggregate), Aggregate, "Aggregation of one or more nested errors surfaced by a result pipeline.", "General"),
        new(nameof(Canceled), Canceled, "Operation canceled by a linked token or cancellation-aware pipeline.", "General"),
        new(nameof(ChannelCompleted), ChannelCompleted, "Channel completed before a pending operation could finish.", "Channels"),
        new(nameof(DeterministicReplay), DeterministicReplay, "Deterministic gate replay attempted to re-execute a captured side effect.", "Deterministic"),
        new(nameof(Exception), Exception, "Unhandled exception surfaced by a result pipeline.", "General"),
        new(nameof(SelectDrained), SelectDrained, "All channel cases completed without yielding a value while selecting.", "Channels"),
        new(nameof(TaskQueueAbandoned), TaskQueueAbandoned, "Task queue operation abandoned after exceeding the retry policy.", "TaskQueue"),
        new(nameof(TaskQueueDeadLettered), TaskQueueDeadLettered, "Task queue work item moved to the dead-letter handler.", "TaskQueue"),
        new(nameof(TaskQueueDisposed), TaskQueueDisposed, "Task queue operations attempted after disposal.", "TaskQueue"),
        new(nameof(TaskQueueLeaseExpired), TaskQueueLeaseExpired, "Lease expired without being completed or extended.", "TaskQueue"),
        new(nameof(TaskQueueLeaseInactive), TaskQueueLeaseInactive, "Lease is no longer active and cannot be manipulated.", "TaskQueue"),
        new(nameof(Timeout), Timeout, "Operation exceeded the allotted deadline.", "General"),
        new(nameof(Unspecified), Unspecified, "Fallback error used when the failure category is unknown.", "General"),
        new(nameof(Validation), Validation, "Input validation failed or precondition was not satisfied.", "General"),
        new(nameof(VersionConflict), VersionConflict, "Deterministic workflow attempted to execute an unsupported version.", "Deterministic"),
    ];

    private static readonly IReadOnlyDictionary<string, ErrorDescriptor> DescriptorMap = CreateDescriptors();

    /// <summary>
    /// Gets a read-only view of all known error descriptors keyed by error code.
    /// </summary>
    public static IReadOnlyDictionary<string, ErrorDescriptor> Descriptors => DescriptorMap;

    /// <summary>
    /// Attempts to resolve descriptive metadata for a well-known error code.
    /// </summary>
    /// <param name="code">The error code to look up.</param>
    /// <param name="descriptor">When this method returns <see langword="true"/>, contains the resolved descriptor.</param>
    /// <returns><see langword="true"/> when the code is known; otherwise <see langword="false"/>.</returns>
    public static bool TryGetDescriptor(string code, out ErrorDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(code);
        return DescriptorMap.TryGetValue(code, out descriptor);
    }

    /// <summary>
    /// Resolves descriptive metadata for a well-known error code.
    /// </summary>
    /// <param name="code">The error code to look up.</param>
    /// <returns>The resolved descriptor.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when the code is not recognized.</exception>
    public static ErrorDescriptor GetDescriptor(string code)
    {
        ArgumentNullException.ThrowIfNull(code);

        if (!DescriptorMap.TryGetValue(code, out ErrorDescriptor descriptor))
        {
            throw new KeyNotFoundException($"Unknown Hugo error code '{code}'.");
        }

        return descriptor;
    }

    private static IReadOnlyDictionary<string, ErrorDescriptor> CreateDescriptors()
    {
        var descriptors = new Dictionary<string, ErrorDescriptor>(AllDescriptors.Length, StringComparer.Ordinal);
        foreach (var descriptor in AllDescriptors)
        {
            descriptors[descriptor.Code] = descriptor;
        }

        return descriptors;
    }
}

/// <summary>
/// Describes a well-known error emitted by Hugo.
/// </summary>
/// <param name="Name">The symbolic name exposed as a constant on <see cref="ErrorCodes"/>.</param>
/// <param name="Code">The string value returned with <see cref="Error.Code"/>.</param>
/// <param name="Description">A human-readable description of the error.</param>
/// <param name="Category">A coarse-grained category used for grouping related errors.</param>
public readonly record struct ErrorDescriptor(string Name, string Code, string Description, string Category);
