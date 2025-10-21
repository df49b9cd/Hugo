using System.Collections.ObjectModel;

namespace Hugo;

/// <summary>
/// Represents a visibility snapshot for a workflow execution.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="WorkflowVisibilityRecord"/> class.
/// </remarks>
public sealed class WorkflowVisibilityRecord(
    string @namespace,
    string workflowId,
    string runId,
    string taskQueue,
    string? scheduleId,
    string? scheduleGroup,
    WorkflowStatus status,
    DateTimeOffset startedAt,
    DateTimeOffset? completedAt,
    long logicalClock,
    int replayCount,
    IReadOnlyDictionary<string, string>? attributes = null)
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(0, StringComparer.Ordinal));

    /// <summary>
    /// Workflow namespace.
    /// </summary>
    public string Namespace { get; } = @namespace ?? throw new ArgumentNullException(nameof(@namespace));

    /// <summary>
    /// Workflow identifier.
    /// </summary>
    public string WorkflowId { get; } = workflowId ?? throw new ArgumentNullException(nameof(workflowId));

    /// <summary>
    /// Workflow run identifier.
    /// </summary>
    public string RunId { get; } = runId ?? throw new ArgumentNullException(nameof(runId));

    /// <summary>
    /// Task queue handling workflow commands.
    /// </summary>
    public string TaskQueue { get; } = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));

    /// <summary>
    /// Optional schedule identifier.
    /// </summary>
    public string? ScheduleId { get; } = scheduleId;

    /// <summary>
    /// Optional schedule grouping identifier.
    /// </summary>
    public string? ScheduleGroup { get; } = scheduleGroup;

    /// <summary>
    /// Current workflow status.
    /// </summary>
    public WorkflowStatus Status { get; } = status;

    /// <summary>
    /// Timestamp when the workflow started.
    /// </summary>
    public DateTimeOffset StartedAt { get; } = startedAt;

    /// <summary>
    /// Timestamp when the workflow completed, if applicable.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; } = completedAt;

    /// <summary>
    /// Latest Lamport logical clock.
    /// </summary>
    public long LogicalClock { get; } = logicalClock;

    /// <summary>
    /// Current replay count observed for the workflow.
    /// </summary>
    public int ReplayCount { get; } = replayCount;

    /// <summary>
    /// Additional attributes associated with the workflow.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes { get; } = attributes is null || attributes.Count == 0
            ? EmptyAttributes
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(attributes, StringComparer.Ordinal));
}

/// <summary>
/// Represents high-level workflow lifecycle states.
/// </summary>
public enum WorkflowStatus
{
    /// <summary>Workflow is in progress.</summary>
    Active = 0,

    /// <summary>Workflow completed successfully.</summary>
    Completed = 1,

    /// <summary>Workflow failed with an error.</summary>
    Failed = 2,

    /// <summary>Workflow was canceled.</summary>
    Canceled = 3,

    /// <summary>Workflow was forcefully terminated.</summary>
    Terminated = 4
}