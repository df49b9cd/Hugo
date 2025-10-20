using System.Collections.ObjectModel;

namespace Hugo;

/// <summary>
/// Represents a visibility snapshot for a workflow execution.
/// </summary>
public sealed class WorkflowVisibilityRecord
{
    private static readonly IReadOnlyDictionary<string, string> EmptyAttributes =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(0, StringComparer.Ordinal));

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkflowVisibilityRecord"/> class.
    /// </summary>
    public WorkflowVisibilityRecord(
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
        Namespace = @namespace ?? throw new ArgumentNullException(nameof(@namespace));
        WorkflowId = workflowId ?? throw new ArgumentNullException(nameof(workflowId));
        RunId = runId ?? throw new ArgumentNullException(nameof(runId));
        TaskQueue = taskQueue ?? throw new ArgumentNullException(nameof(taskQueue));
        ScheduleId = scheduleId;
        ScheduleGroup = scheduleGroup;
        Status = status;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        LogicalClock = logicalClock;
        ReplayCount = replayCount;
        Attributes = attributes is null || attributes.Count == 0
            ? EmptyAttributes
            : new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(attributes, StringComparer.Ordinal));
    }

    /// <summary>
    /// Workflow namespace.
    /// </summary>
    public string Namespace { get; }

    /// <summary>
    /// Workflow identifier.
    /// </summary>
    public string WorkflowId { get; }

    /// <summary>
    /// Workflow run identifier.
    /// </summary>
    public string RunId { get; }

    /// <summary>
    /// Task queue handling workflow commands.
    /// </summary>
    public string TaskQueue { get; }

    /// <summary>
    /// Optional schedule identifier.
    /// </summary>
    public string? ScheduleId { get; }

    /// <summary>
    /// Optional schedule grouping identifier.
    /// </summary>
    public string? ScheduleGroup { get; }

    /// <summary>
    /// Current workflow status.
    /// </summary>
    public WorkflowStatus Status { get; }

    /// <summary>
    /// Timestamp when the workflow started.
    /// </summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>
    /// Timestamp when the workflow completed, if applicable.
    /// </summary>
    public DateTimeOffset? CompletedAt { get; }

    /// <summary>
    /// Latest Lamport logical clock.
    /// </summary>
    public long LogicalClock { get; }

    /// <summary>
    /// Current replay count observed for the workflow.
    /// </summary>
    public int ReplayCount { get; }

    /// <summary>
    /// Additional attributes associated with the workflow.
    /// </summary>
    public IReadOnlyDictionary<string, string> Attributes { get; }
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