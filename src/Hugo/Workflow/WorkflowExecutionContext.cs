using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Hugo;

/// <summary>
/// Carries ambient workflow execution metadata including logical clock state for deterministic orchestration.
/// </summary>
public sealed class WorkflowExecutionContext
{
    private static readonly IReadOnlyDictionary<string, string> EmptyMetadata =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(0, StringComparer.Ordinal));

    private readonly IReadOnlyDictionary<string, string> _metadata;
    private int _started;
    private long _logicalClock;
    private int _replayCount;
    private int _completionStatusValue;
    private long _completedAtUtcTicks;
    private Error? _completionError;
    private Activity? _activity;

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkflowExecutionContext"/> class.
    /// </summary>
    /// <param name="@namespace">Workflow namespace identifier.</param>
    /// <param name="workflowId">Stable workflow identifier.</param>
    /// <param name="runId">Unique run identifier for the current execution.</param>
    /// <param name="taskQueue">Name of the task queue processing workflow commands.</param>
    /// <param name="scheduleId">Optional schedule identifier associated with this run.</param>
    /// <param name="scheduleGroup">Optional schedule grouping identifier.</param>
    /// <param name="metadata">Additional namespace-level metadata attached to the execution.</param>
    /// <param name="timeProvider">Optional <see cref="TimeProvider"/> to support deterministic time.</param>
    /// <param name="startedAt">Optional start timestamp; defaults to the current UTC time.</param>
    /// <param name="initialLogicalClock">Initial Lamport clock value (defaults to zero).</param>
    /// <param name="replayCount">Initial workflow replay count.</param>
    public WorkflowExecutionContext(
        string @namespace,
        string workflowId,
        string runId,
        string taskQueue,
        string? scheduleId = null,
        string? scheduleGroup = null,
        IEnumerable<KeyValuePair<string, string>>? metadata = null,
        TimeProvider? timeProvider = null,
        DateTimeOffset? startedAt = null,
        long initialLogicalClock = 0,
        int replayCount = 0)
    {
        Namespace = ValidateName(@namespace, nameof(@namespace));
        WorkflowId = ValidateName(workflowId, nameof(workflowId));
        RunId = ValidateName(runId, nameof(runId));
        TaskQueue = ValidateName(taskQueue, nameof(taskQueue));
        ScheduleId = string.IsNullOrWhiteSpace(scheduleId) ? null : scheduleId;
        ScheduleGroup = string.IsNullOrWhiteSpace(scheduleGroup) ? null : scheduleGroup;
        TimeProvider = timeProvider ?? TimeProvider.System;
        StartedAt = (startedAt ?? TimeProvider.GetUtcNow()).ToUniversalTime();

        if (initialLogicalClock < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(initialLogicalClock), initialLogicalClock, "Logical clock must be non-negative.");
        }

        if (replayCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(replayCount), replayCount, "Replay count must be non-negative.");
        }

        _logicalClock = initialLogicalClock;
        _replayCount = replayCount;
        _metadata = CreateMetadata(metadata);
    }

    /// <summary>
    /// Gets the namespace associated with the workflow execution.
    /// </summary>
    public string Namespace { get; }

    /// <summary>
    /// Gets the workflow identifier.
    /// </summary>
    public string WorkflowId { get; }

    /// <summary>
    /// Gets the run identifier for this execution attempt.
    /// </summary>
    public string RunId { get; }

    /// <summary>
    /// Gets the task queue responsible for processing workflow commands.
    /// </summary>
    public string TaskQueue { get; }

    /// <summary>
    /// Gets the optional schedule identifier.
    /// </summary>
    public string? ScheduleId { get; }

    /// <summary>
    /// Gets the optional schedule grouping identifier.
    /// </summary>
    public string? ScheduleGroup { get; }

    /// <summary>
    /// Gets the metadata associated with the execution.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata => _metadata;

    /// <summary>
    /// Gets the time provider associated with the execution.
    /// </summary>
    public TimeProvider TimeProvider { get; }

    /// <summary>
    /// Gets the timestamp when the workflow started.
    /// </summary>
    public DateTimeOffset StartedAt { get; }
    /// <summary>
    /// Gets the completion timestamp when the workflow finished, otherwise <c>null</c> while in flight.
    /// </summary>
    public DateTimeOffset? CompletedAt => _completedAtUtcTicks == 0 ? null : new DateTimeOffset(_completedAtUtcTicks, TimeSpan.Zero);

    /// <summary>
    /// Gets the completion status when available.
    /// </summary>
    public WorkflowCompletionStatus? CompletionStatus => _completionStatusValue == 0 ? null : (WorkflowCompletionStatus)_completionStatusValue;

    /// <summary>
    /// Gets the enriched completion error when the workflow concluded with a failure.
    /// </summary>
    public Error? CompletionError => _completionError;

    /// <summary>
    /// Ensures instrumentation is recorded for the start of the workflow. Subsequent invocations are ignored.
    /// </summary>
    internal void MarkStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        var logicalClock = Tick();
        GoDiagnostics.RecordWorkflowStarted(this, logicalClock);
        _activity = GoDiagnostics.StartWorkflowActivity(this, logicalClock);
    }

    /// <summary>
    /// Produces a visibility snapshot describing the current workflow state.
    /// </summary>
    public WorkflowVisibilityRecord SnapshotVisibility(IEnumerable<KeyValuePair<string, string>>? attributes = null)
    {
        var status = CompletionStatus is null ? WorkflowStatus.Active : MapStatus(CompletionStatus.Value);
        var logicalClock = LogicalClock;
        return BuildVisibilityRecord(status, CompletedAt, logicalClock, attributes, CompletionError);
    }

    /// <summary>
    /// Completes the workflow and records diagnostics, throwing when the workflow has already finished.
    /// </summary>
    public WorkflowVisibilityRecord Complete(WorkflowCompletionStatus status, Error? error = null, IEnumerable<KeyValuePair<string, string>>? attributes = null)
    {
        if (!TryComplete(status, error, attributes, out var record))
        {
            throw new InvalidOperationException("Workflow execution already completed.");
        }

        return record;
    }

    /// <summary>
    /// Attempts to complete the workflow and record diagnostics.
    /// </summary>
    internal bool TryComplete(WorkflowCompletionStatus status, Error? error, IEnumerable<KeyValuePair<string, string>>? attributes, out WorkflowVisibilityRecord record)
    {
        var logicalClock = Tick();
        var enrichedError = error is null ? null : AttachError(error, logicalClock);
        var completedAt = TimeProvider.GetUtcNow().ToUniversalTime();
        var statusValue = (int)status;

        if (Interlocked.CompareExchange(ref _completionStatusValue, statusValue, 0) != 0)
        {
            record = default!;
            return false;
        }

        Interlocked.Exchange(ref _completedAtUtcTicks, completedAt.UtcTicks);
        if (enrichedError is not null)
        {
            Interlocked.CompareExchange(ref _completionError, enrichedError, null);
        }

        GoDiagnostics.RecordWorkflowCompleted(this, status, completedAt - StartedAt, logicalClock, enrichedError);
        GoDiagnostics.CompleteWorkflowActivity(_activity, this, status, logicalClock, completedAt, enrichedError);
        _activity = null;

        record = BuildVisibilityRecord(MapStatus(status), completedAt, logicalClock, attributes, enrichedError);
        return true;
    }

    /// <summary>
    /// Gets the current Lamport logical clock value.
    /// </summary>
    public long LogicalClock => Interlocked.Read(ref _logicalClock);

    /// <summary>
    /// Gets the number of times the workflow has replayed.
    /// </summary>
    public int ReplayCount => Volatile.Read(ref _replayCount);

    /// <summary>
    /// Increments the logical clock for a local event and returns the new value.
    /// </summary>
    public long Tick() => Interlocked.Increment(ref _logicalClock);

    /// <summary>
    /// Observes an external logical clock and advances this clock accordingly.
    /// </summary>
    /// <param name="observed">Observed logical clock value.</param>
    /// <returns>The updated logical clock.</returns>
    public long Observe(long observed)
    {
        if (observed < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(observed), observed, "Observed clock must be non-negative.");
        }

        while (true)
        {
            var current = Interlocked.Read(ref _logicalClock);
            var candidate = Math.Max(current, observed) + 1;
            if (Interlocked.CompareExchange(ref _logicalClock, candidate, current) == current)
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Resets the logical clock to the supplied value when performing deterministic rewinds.
    /// </summary>
    /// <param name="value">New logical clock value.</param>
    public void ResetLogicalClock(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Logical clock must be non-negative.");
        }

        Interlocked.Exchange(ref _logicalClock, value);
    }

    /// <summary>
    /// Increments the replay counter and returns the updated value.
    /// </summary>
    public int IncrementReplayCount() => Interlocked.Increment(ref _replayCount);

    /// <summary>
    /// Attempts to read a metadata value using the supplied key.
    /// </summary>
    /// <param name="key">Metadata key.</param>
    /// <param name="value">Resolved value when present.</param>
    /// <returns><c>true</c> when the key exists; otherwise <c>false</c>.</returns>
    public bool TryGetMetadata(string key, [NotNullWhen(true)] out string? value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_metadata.TryGetValue(key, out var resolved))
        {
            value = resolved;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// Enriches an error with workflow metadata.
    /// </summary>
    internal Error AttachError(Error error, long logicalClock)
    {
        ArgumentNullException.ThrowIfNull(error);

        var metadata = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["workflow.namespace"] = Namespace,
            ["workflow.id"] = WorkflowId,
            ["workflow.run_id"] = RunId,
            ["workflow.task_queue"] = TaskQueue,
            ["workflow.logical_clock"] = logicalClock,
            ["workflow.replay_count"] = ReplayCount
        };

        if (ScheduleId is not null)
        {
            metadata["workflow.schedule_id"] = ScheduleId;
        }

        if (ScheduleGroup is not null)
        {
            metadata["workflow.schedule_group"] = ScheduleGroup;
        }

        foreach (var entry in _metadata)
        {
            metadata[$"workflow.metadata.{entry.Key}"] = entry.Value;
        }

        return error.WithMetadata(metadata);
    }

    private WorkflowVisibilityRecord BuildVisibilityRecord(
        WorkflowStatus status,
        DateTimeOffset? completedAt,
        long logicalClock,
        IEnumerable<KeyValuePair<string, string>>? attributes,
        Error? error)
    {
        var builder = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in _metadata)
        {
            builder[entry.Key] = entry.Value;
        }

        if (error is not null)
        {
            builder["error.code"] = error.Code ?? string.Empty;
            builder["error.message"] = error.Message;
        }

        if (attributes is not null)
        {
            foreach (var entry in attributes)
            {
                if (string.IsNullOrWhiteSpace(entry.Key))
                {
                    continue;
                }

                builder[entry.Key] = entry.Value ?? string.Empty;
            }
        }

        var attributeSnapshot = builder.Count == 0
            ? EmptyMetadata
            : new ReadOnlyDictionary<string, string>(builder);

        return new WorkflowVisibilityRecord(
            Namespace,
            WorkflowId,
            RunId,
            TaskQueue,
            ScheduleId,
            ScheduleGroup,
            status,
            StartedAt,
            completedAt,
            logicalClock,
            ReplayCount,
            attributeSnapshot);
    }

    private static WorkflowStatus MapStatus(WorkflowCompletionStatus status) => status switch
    {
        WorkflowCompletionStatus.Completed => WorkflowStatus.Completed,
        WorkflowCompletionStatus.Failed => WorkflowStatus.Failed,
        WorkflowCompletionStatus.Canceled => WorkflowStatus.Canceled,
        WorkflowCompletionStatus.Terminated => WorkflowStatus.Terminated,
        _ => WorkflowStatus.Active
    };

    private static string ValidateName(string value, string paramName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, paramName);
        return value;
    }

    private static IReadOnlyDictionary<string, string> CreateMetadata(IEnumerable<KeyValuePair<string, string>>? metadata)
    {
        if (metadata is null)
        {
            return EmptyMetadata;
        }

        var dictionary = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in metadata)
        {
            if (string.IsNullOrWhiteSpace(entry.Key))
            {
                throw new ArgumentException("Metadata keys must be non-empty.", nameof(metadata));
            }

            dictionary[entry.Key] = entry.Value ?? string.Empty;
        }

        return dictionary.Count == 0 ? EmptyMetadata : new ReadOnlyDictionary<string, string>(dictionary);
    }
}

/// <summary>
/// Provides ambient scope helpers for workflow execution contexts.
/// </summary>
public static class WorkflowExecution
{
    private static readonly AsyncLocal<AmbientContext?> Ambient = new();

    /// <summary>
    /// Gets the current workflow execution context, if any.
    /// </summary>
    public static WorkflowExecutionContext? Current => Ambient.Value?.Context;

    /// <summary>
    /// Gets the cancellation token associated with the current workflow execution scope.
    /// </summary>
    public static CancellationToken CurrentCancellationToken => Ambient.Value?.CancellationToken ?? CancellationToken.None;

    /// <summary>
    /// Indicates whether a workflow execution context is currently attached.
    /// </summary>
    public static bool HasCurrent => Ambient.Value?.Context is not null;

    /// <summary>
    /// Creates a new ambient workflow execution scope.
    /// </summary>
    /// <param name="context">Context to attach.</param>
    /// <param name="replace">When true, replaces any existing ambient context instead of stacking.</param>
    /// <param name="cancellationToken">Cancellation token linked to the scope.</param>
    public static Scope Enter(WorkflowExecutionContext context, bool replace = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.MarkStarted();

        var parent = Ambient.Value;
        var nextParent = replace ? parent?.Parent : parent;
        var ambient = new AmbientContext(context, cancellationToken, nextParent);
        Ambient.Value = ambient;
        return new Scope(ambient);
    }

    /// <summary>
    /// Retrieves the current workflow execution context, throwing when absent.
    /// </summary>
    public static WorkflowExecutionContext RequireCurrent() => Current ?? throw new InvalidOperationException("No workflow execution context is associated with the current logical flow of execution.");

    private static void Exit(AmbientContext ambient)
    {
        if (!ReferenceEquals(Ambient.Value, ambient))
        {
            throw new InvalidOperationException("Workflow execution scope disposed out of order.");
        }

        Ambient.Value = ambient.Parent;
    }

    internal sealed class AmbientContext
    {
        [SuppressMessage("Design", "CA1068:CancellationToken parameters must come last", Justification = "Constructor mirrors public Enter signature for consistency.")]
        internal AmbientContext(WorkflowExecutionContext context, CancellationToken cancellationToken, AmbientContext? parent)
        {
            Context = context;
            CancellationToken = cancellationToken;
            Parent = parent;
        }

        internal WorkflowExecutionContext Context { get; }

        internal CancellationToken CancellationToken { get; }

        internal AmbientContext? Parent { get; }
    }

    /// <summary>
    /// Represents a disposable scope that manages the ambient workflow execution context.
    /// </summary>
    public sealed class Scope : IAsyncDisposable, IDisposable
    {
        private readonly AmbientContext _ambient;
        private bool _disposed;

        internal Scope(AmbientContext ambient)
        {
            _ambient = ambient;
        }

        /// <summary>
        /// Gets the context associated with this scope.
        /// </summary>
        public WorkflowExecutionContext Context => _ambient.Context;

        /// <summary>
        /// Gets the cancellation token associated with this scope.
        /// </summary>
        public CancellationToken CancellationToken => _ambient.CancellationToken;

        /// <summary>
        /// Completes the workflow using the underlying context.
        /// </summary>
        public WorkflowVisibilityRecord Complete(WorkflowCompletionStatus status, Error? error = null, IEnumerable<KeyValuePair<string, string>>? attributes = null) =>
            _ambient.Context.Complete(status, error, attributes);

        /// <summary>
        /// Attempts to complete the workflow using the underlying context.
        /// </summary>
        public bool TryComplete(WorkflowCompletionStatus status, Error? error, IEnumerable<KeyValuePair<string, string>>? attributes, out WorkflowVisibilityRecord record) =>
            _ambient.Context.TryComplete(status, error, attributes, out record);

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Exit(_ambient);
            GC.SuppressFinalize(this);
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Indicates how a workflow execution concluded.
/// </summary>
public enum WorkflowCompletionStatus
{
    /// <summary>Workflow finished successfully.</summary>
    Completed = 1,

    /// <summary>Workflow terminated due to an error.</summary>
    Failed = 2,

    /// <summary>Workflow was canceled.</summary>
    Canceled = 3,

    /// <summary>Workflow was forcefully terminated.</summary>
    Terminated = 4
}
