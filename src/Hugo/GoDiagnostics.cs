using System.Diagnostics;
using System.Diagnostics.Metrics;
using Hugo;

namespace Hugo;

/// <summary>
/// Provides optional instrumentation hooks for Go-inspired primitives.
/// </summary>
public static class GoDiagnostics
{
    private const string DefaultMeterName = "Hugo.Go";

    private static readonly Lock Sync = new();

    private static Meter? _meter;
    private static ActivitySource? _activitySource;
    private static Counter<long>? _waitGroupAdditions;
    private static Counter<long>? _waitGroupCompletions;
    private static Counter<long>? _resultSuccesses;
    private static Counter<long>? _resultFailures;
    private static UpDownCounter<long>? _waitGroupOutstanding;
    private static Counter<long>? _channelSelectAttempts;
    private static Counter<long>? _channelSelectCompletions;
    private static Counter<long>? _channelSelectTimeouts;
    private static Counter<long>? _channelSelectCancellations;
    private static Histogram<double>? _channelSelectLatency;
    private static Histogram<long>? _channelDepth;
    private static Counter<long>? _taskQueueEnqueued;
    private static Counter<long>? _taskQueueLeased;
    private static Counter<long>? _taskQueueCompleted;
    private static Counter<long>? _taskQueueHeartbeats;
    private static Counter<long>? _taskQueueFailed;
    private static Counter<long>? _taskQueueExpired;
    private static Counter<long>? _taskQueueRequeued;
    private static Counter<long>? _taskQueueDeadLettered;
    private static UpDownCounter<long>? _taskQueuePending;
    private static UpDownCounter<long>? _taskQueueActive;
    private static Histogram<long>? _taskQueuePendingDepth;
    private static Histogram<long>? _taskQueueActiveDepth;
    private static Histogram<double>? _taskQueueLeaseDuration;
    private static Histogram<double>? _taskQueueHeartbeatExtension;
    private static Counter<long>? _workflowStarted;
    private static Counter<long>? _workflowCompleted;
    private static Counter<long>? _workflowFailed;
    private static Counter<long>? _workflowCanceled;
    private static Counter<long>? _workflowTerminated;
    private static UpDownCounter<long>? _workflowActive;
    private static Histogram<long>? _workflowReplayCount;
    private static Histogram<long>? _workflowLogicalClock;
    private static Histogram<double>? _workflowDuration;

    /// <summary>
    /// Configures instrumentation using the supplied <see cref="IMeterFactory"/>.
    /// </summary>
    public static void Configure(IMeterFactory factory, string? meterName = null)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var options = new MeterOptions(meterName ?? DefaultMeterName);
        Configure(factory.Create(options));
    }

    /// <summary>
    /// Configures instrumentation using the provided <see cref="Meter"/> instance.
    /// </summary>
    public static void Configure(Meter meter)
    {
        ArgumentNullException.ThrowIfNull(meter);

        lock (Sync)
        {
            _meter = meter;
            _waitGroupAdditions = meter.CreateCounter<long>("waitgroup.additions", unit: "operations", description: "Number of WaitGroup.Add invocations.");
            _waitGroupCompletions = meter.CreateCounter<long>("waitgroup.completions", unit: "operations", description: "Number of WaitGroup.Done invocations.");
            _resultSuccesses = meter.CreateCounter<long>("result.successes", unit: "events", description: "Number of successful Result instances created.");
            _resultFailures = meter.CreateCounter<long>("result.failures", unit: "events", description: "Number of failed Result instances created.");
            _waitGroupOutstanding = meter.CreateUpDownCounter<long>("waitgroup.outstanding", unit: "tasks", description: "Current number of outstanding WaitGroup operations.");
            _channelSelectAttempts = meter.CreateCounter<long>("channel.select.attempts", unit: "operations", description: "Number of Go.Select attempts.");
            _channelSelectCompletions = meter.CreateCounter<long>("channel.select.completions", unit: "operations", description: "Number of Go.Select operations that completed with a value.");
            _channelSelectTimeouts = meter.CreateCounter<long>("channel.select.timeouts", unit: "operations", description: "Number of Go.Select operations that timed out.");
            _channelSelectCancellations = meter.CreateCounter<long>("channel.select.cancellations", unit: "operations", description: "Number of Go.Select operations that were canceled.");
            _channelSelectLatency = meter.CreateHistogram<double>("channel.select.latency", unit: "ms", description: "Latency of Go.Select operations.");
            _channelDepth = meter.CreateHistogram<long>("channel.depth", unit: "items", description: "Observed depth of channels when values are consumed.");
            _taskQueueEnqueued = meter.CreateCounter<long>("taskqueue.enqueued", unit: "items", description: "Number of work items enqueued on task queues.");
            _taskQueueLeased = meter.CreateCounter<long>("taskqueue.leased", unit: "leases", description: "Number of leases granted by task queues.");
            _taskQueueCompleted = meter.CreateCounter<long>("taskqueue.completed", unit: "leases", description: "Number of leases completed successfully.");
            _taskQueueHeartbeats = meter.CreateCounter<long>("taskqueue.heartbeats", unit: "leases", description: "Number of lease heartbeat renewals.");
            _taskQueueFailed = meter.CreateCounter<long>("taskqueue.failed", unit: "leases", description: "Number of leases that failed.");
            _taskQueueExpired = meter.CreateCounter<long>("taskqueue.expired", unit: "leases", description: "Number of leases that expired without renewal.");
            _taskQueueRequeued = meter.CreateCounter<long>("taskqueue.requeued", unit: "items", description: "Number of work items requeued after a failure or expiration.");
            _taskQueueDeadLettered = meter.CreateCounter<long>("taskqueue.deadlettered", unit: "items", description: "Number of work items routed to the dead-letter handler.");
            _taskQueuePending = meter.CreateUpDownCounter<long>("taskqueue.pending", unit: "items", description: "Current number of pending work items queued for leasing.");
            _taskQueueActive = meter.CreateUpDownCounter<long>("taskqueue.active", unit: "leases", description: "Current number of active leases in flight.");
            _taskQueuePendingDepth = meter.CreateHistogram<long>("taskqueue.pending.depth", unit: "items", description: "Observed pending backlog when queue operations occur.");
            _taskQueueActiveDepth = meter.CreateHistogram<long>("taskqueue.active.depth", unit: "leases", description: "Observed active lease count when queue operations occur.");
            _taskQueueLeaseDuration = meter.CreateHistogram<double>("taskqueue.lease.duration", unit: "ms", description: "Observed durations between lease grant and completion.");
            _taskQueueHeartbeatExtension = meter.CreateHistogram<double>("taskqueue.heartbeat.extension", unit: "ms", description: "Observed extensions granted by lease heartbeats.");
            _workflowStarted = meter.CreateCounter<long>("workflow.started", unit: "workflows", description: "Number of workflow executions started.");
            _workflowCompleted = meter.CreateCounter<long>("workflow.completed", unit: "workflows", description: "Number of workflow executions that completed successfully.");
            _workflowFailed = meter.CreateCounter<long>("workflow.failed", unit: "workflows", description: "Number of workflow executions that failed.");
            _workflowCanceled = meter.CreateCounter<long>("workflow.canceled", unit: "workflows", description: "Number of workflow executions that were canceled.");
            _workflowTerminated = meter.CreateCounter<long>("workflow.terminated", unit: "workflows", description: "Number of workflow executions that were terminated.");
            _workflowActive = meter.CreateUpDownCounter<long>("workflow.active", unit: "workflows", description: "Current number of active workflow executions.");
            _workflowReplayCount = meter.CreateHistogram<long>("workflow.replay.count", unit: "replays", description: "Observed workflow replay counts.");
            _workflowLogicalClock = meter.CreateHistogram<long>("workflow.logical.clock", unit: "ticks", description: "Observed workflow logical clock values.");
            _workflowDuration = meter.CreateHistogram<double>("workflow.duration", unit: "ms", description: "Observed workflow execution durations.");
        }
    }

    /// <summary>
    /// Configures distributed tracing instrumentation using the provided <see cref="ActivitySource"/>.
    /// </summary>
    public static void Configure(ActivitySource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        lock (Sync)
        {
            _activitySource = source;
        }
    }

    /// <summary>
    /// Configures metrics and distributed tracing instrumentation.
    /// </summary>
    public static void Configure(Meter meter, ActivitySource activitySource)
    {
        ArgumentNullException.ThrowIfNull(activitySource);

        Configure(meter);
        Configure(activitySource);
    }

    /// <summary>
    /// Resets instrumentation to a no-op state. Intended for unit testing.
    /// </summary>
    public static void Reset()
    {
        lock (Sync)
        {
            _meter?.Dispose();
            _meter = null;
            _activitySource = null;
            _waitGroupAdditions = null;
            _waitGroupCompletions = null;
            _resultSuccesses = null;
            _resultFailures = null;
            _waitGroupOutstanding = null;
            _channelSelectAttempts = null;
            _channelSelectCompletions = null;
            _channelSelectTimeouts = null;
            _channelSelectCancellations = null;
            _channelSelectLatency = null;
            _channelDepth = null;
            _taskQueueEnqueued = null;
            _taskQueueLeased = null;
            _taskQueueCompleted = null;
            _taskQueueHeartbeats = null;
            _taskQueueFailed = null;
            _taskQueueExpired = null;
            _taskQueueRequeued = null;
            _taskQueueDeadLettered = null;
            _taskQueuePending = null;
            _taskQueueActive = null;
            _taskQueuePendingDepth = null;
            _taskQueueActiveDepth = null;
            _taskQueueLeaseDuration = null;
            _taskQueueHeartbeatExtension = null;
            _workflowStarted = null;
            _workflowCompleted = null;
            _workflowFailed = null;
            _workflowCanceled = null;
            _workflowTerminated = null;
            _workflowActive = null;
            _workflowReplayCount = null;
            _workflowLogicalClock = null;
            _workflowDuration = null;
        }
    }

    internal static void RecordWorkflowStarted(WorkflowExecutionContext context, long logicalClock)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tags = CreateWorkflowTags(context);
        _workflowStarted?.Add(1, tags);
        _workflowActive?.Add(1, tags);
        _workflowReplayCount?.Record(context.ReplayCount, tags);
        _workflowLogicalClock?.Record(logicalClock, tags);
    }

    internal static Activity? StartWorkflowActivity(WorkflowExecutionContext context, long logicalClock)
    {
        ArgumentNullException.ThrowIfNull(context);

        var source = _activitySource;
        if (source is null)
        {
            return null;
        }

        var activity = source.StartActivity("Hugo.Workflow", ActivityKind.Internal);
        if (activity is null)
        {
            return null;
        }

        if (activity.IsAllDataRequested)
        {
            activity.SetTag("workflow.namespace", context.Namespace);
            activity.SetTag("workflow.id", context.WorkflowId);
            activity.SetTag("workflow.run_id", context.RunId);
            activity.SetTag("workflow.task_queue", context.TaskQueue);
            activity.SetTag("workflow.logical_clock", logicalClock);
            activity.SetTag("workflow.replay_count", context.ReplayCount);

            if (context.ScheduleId is not null)
            {
                activity.SetTag("workflow.schedule_id", context.ScheduleId);
            }

            if (context.ScheduleGroup is not null)
            {
                activity.SetTag("workflow.schedule_group", context.ScheduleGroup);
            }

            foreach (var entry in context.Metadata)
            {
                activity.SetTag($"workflow.metadata.{entry.Key}", entry.Value);
            }
        }

        return activity;
    }

    internal static void RecordWorkflowCompleted(
        WorkflowExecutionContext context,
        WorkflowCompletionStatus status,
        TimeSpan duration,
        long logicalClock,
        Error? error)
    {
        ArgumentNullException.ThrowIfNull(context);

        var tags = CreateWorkflowTags(context);
        var tagsWithStatus = tags;
        tagsWithStatus.Add("workflow.status", status.ToString().ToLowerInvariant());

        _workflowActive?.Add(-1, tags);
        _workflowDuration?.Record(Math.Max(duration.TotalMilliseconds, 0d), tagsWithStatus);
        _workflowReplayCount?.Record(context.ReplayCount, tagsWithStatus);
        _workflowLogicalClock?.Record(logicalClock, tagsWithStatus);

        switch (status)
        {
            case WorkflowCompletionStatus.Completed:
                _workflowCompleted?.Add(1, tagsWithStatus);
                break;
            case WorkflowCompletionStatus.Failed:
                _workflowFailed?.Add(1, tagsWithStatus);
                break;
            case WorkflowCompletionStatus.Canceled:
                _workflowCanceled?.Add(1, tagsWithStatus);
                break;
            case WorkflowCompletionStatus.Terminated:
                _workflowTerminated?.Add(1, tagsWithStatus);
                break;
        }
    }

    internal static void CompleteWorkflowActivity(
        Activity? activity,
        WorkflowExecutionContext context,
        WorkflowCompletionStatus status,
        long logicalClock,
        DateTimeOffset completedAt,
        Error? error)
    {
        if (activity is null)
        {
            return;
        }

        if (activity.IsAllDataRequested)
        {
            activity.SetTag("workflow.logical_clock", logicalClock);
            activity.SetTag("workflow.status", status.ToString().ToLowerInvariant());
            activity.SetTag("workflow.replay_count", context.ReplayCount);

            if (error is not null)
            {
                if (!string.IsNullOrEmpty(error.Code))
                {
                    activity.SetTag("workflow.error.code", error.Code);
                }

                if (!string.IsNullOrEmpty(error.Message))
                {
                    activity.SetTag("workflow.error.message", error.Message);
                }
            }
        }

        if (error is null)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Error, error.Message);
        }

        activity.SetEndTime(completedAt.UtcDateTime);
        activity.Stop();
    }

    private static TagList CreateWorkflowTags(WorkflowExecutionContext context)
    {
        var tags = new TagList();
        tags.Add("workflow.namespace", context.Namespace);
        tags.Add("workflow.id", context.WorkflowId);
        tags.Add("workflow.run_id", context.RunId);
        tags.Add("workflow.task_queue", context.TaskQueue);

        if (context.ScheduleId is not null)
        {
            tags.Add("workflow.schedule_id", context.ScheduleId);
        }

        if (context.ScheduleGroup is not null)
        {
            tags.Add("workflow.schedule_group", context.ScheduleGroup);
        }

        return tags;
    }

    internal static void RecordWaitGroupAdd(int delta)
    {
        if (delta <= 0)
        {
            return;
        }

        _waitGroupAdditions?.Add(delta);
        _waitGroupOutstanding?.Add(delta);
    }

    internal static void RecordWaitGroupDone()
    {
        _waitGroupCompletions?.Add(1);
        _waitGroupOutstanding?.Add(-1);
    }

    internal static void RecordResultSuccess() => _resultSuccesses?.Add(1);

    internal static void RecordResultFailure() => _resultFailures?.Add(1);

    internal static void RecordChannelSelectAttempt(Activity? activity = null)
    {
        _channelSelectAttempts?.Add(1);

        if (activity is not null)
        {
            activity.AddEvent(new ActivityEvent("select.attempt"));
        }
    }

    internal static void RecordChannelSelectCompleted(TimeSpan duration, Activity? activity = null, Error? error = null)
    {
        _channelSelectCompletions?.Add(1);
        _channelSelectLatency?.Record(duration.TotalMilliseconds);

        if (activity is null)
        {
            return;
        }

        if (activity.IsAllDataRequested)
        {
            activity.SetTag("hugo.select.duration_ms", duration.TotalMilliseconds);
            activity.SetTag("hugo.select.outcome", error is null ? "completed" : "error");

            if (error is not null)
            {
                if (!string.IsNullOrEmpty(error.Code))
                {
                    activity.SetTag("hugo.error.code", error.Code);
                }

                if (!string.IsNullOrEmpty(error.Message))
                {
                    activity.SetTag("hugo.error.message", error.Message);
                }
            }
        }

        if (error is null)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }
        else
        {
            activity.SetStatus(ActivityStatusCode.Error, error.Message);
        }
    }

    internal static void RecordChannelSelectTimeout(TimeSpan duration, Activity? activity = null)
    {
        _channelSelectTimeouts?.Add(1);
        _channelSelectLatency?.Record(duration.TotalMilliseconds);

        if (activity is null)
        {
            return;
        }

        if (activity.IsAllDataRequested)
        {
            activity.SetTag("hugo.select.duration_ms", duration.TotalMilliseconds);
            activity.SetTag("hugo.select.outcome", "timeout");
        }

        activity.SetStatus(ActivityStatusCode.Error, "Timeout");
    }

    internal static void RecordChannelSelectCanceled(TimeSpan duration, Activity? activity = null, bool cancellationFromCaller = true)
    {
        _channelSelectCancellations?.Add(1);
        _channelSelectLatency?.Record(duration.TotalMilliseconds);

        if (activity is null)
        {
            return;
        }

        if (activity.IsAllDataRequested)
        {
            activity.SetTag("hugo.select.duration_ms", duration.TotalMilliseconds);
            activity.SetTag("hugo.select.outcome", "canceled");
            activity.SetTag("hugo.select.canceled.byCaller", cancellationFromCaller);
        }

        activity.SetStatus(ActivityStatusCode.Error, cancellationFromCaller ? "Canceled by caller." : "Canceled.");
    }

    internal static Activity? StartSelectActivity(int caseCount, TimeSpan timeout)
    {
        var source = _activitySource;
        var activity = source?.StartActivity("Go.Select", ActivityKind.Internal);

        if (activity is null)
        {
            return null;
        }

        if (activity.IsAllDataRequested)
        {
            activity.SetTag("hugo.select.case_count", caseCount);

            if (timeout == Timeout.InfiniteTimeSpan)
            {
                activity.SetTag("hugo.select.has_timeout", false);
                activity.SetTag("hugo.select.timeout_ms", double.PositiveInfinity);
            }
            else
            {
                activity.SetTag("hugo.select.has_timeout", true);
                activity.SetTag("hugo.select.timeout_ms", timeout.TotalMilliseconds);
            }
        }

        return activity;
    }

    internal static void RecordChannelDepth(long depth)
    {
        if (depth < 0)
        {
            return;
        }

        _channelDepth?.Record(depth);
    }

    internal static void RecordTaskQueueQueued(long pendingDepth)
    {
        if (pendingDepth < 0)
        {
            return;
        }

        _taskQueueEnqueued?.Add(1);
        _taskQueuePending?.Add(1);
        _taskQueuePendingDepth?.Record(pendingDepth);
    }

    internal static void RecordTaskQueueLeased(int attempt, long pendingDepth, int activeLeases)
    {
        _ = attempt;
        _taskQueueLeased?.Add(1);
        _taskQueuePending?.Add(-1);
        _taskQueueActive?.Add(1);
        _taskQueuePendingDepth?.Record(pendingDepth);
        _taskQueueActiveDepth?.Record(activeLeases);
    }

    internal static void RecordTaskQueueCompleted(int attempt, TimeSpan leaseDuration, long pendingDepth, int activeLeases)
    {
        _ = attempt;
        _taskQueueCompleted?.Add(1);
        _taskQueueActive?.Add(-1);
        _taskQueueLeaseDuration?.Record(leaseDuration.TotalMilliseconds);
        _taskQueuePendingDepth?.Record(pendingDepth);
        _taskQueueActiveDepth?.Record(activeLeases);
    }

    internal static void RecordTaskQueueHeartbeat(int attempt, TimeSpan extension, int activeLeases)
    {
        _ = attempt;
        _taskQueueHeartbeats?.Add(1);
        _taskQueueHeartbeatExtension?.Record(extension.TotalMilliseconds);
        _taskQueueActiveDepth?.Record(activeLeases);
    }

    internal static void RecordTaskQueueFailed(int attempt, TimeSpan leaseDuration, long pendingDepth, int activeLeases)
    {
        _ = attempt;
        _taskQueueFailed?.Add(1);
        _taskQueueActive?.Add(-1);
        _taskQueueLeaseDuration?.Record(leaseDuration.TotalMilliseconds);
        _taskQueuePendingDepth?.Record(pendingDepth);
        _taskQueueActiveDepth?.Record(activeLeases);
    }

    internal static void RecordTaskQueueExpired(int attempt, TimeSpan leaseDuration, long pendingDepth, int activeLeases)
    {
        _ = attempt;
        _taskQueueExpired?.Add(1);
        _taskQueueActive?.Add(-1);
        _taskQueueLeaseDuration?.Record(leaseDuration.TotalMilliseconds);
        _taskQueuePendingDepth?.Record(pendingDepth);
        _taskQueueActiveDepth?.Record(activeLeases);
    }

    internal static void RecordTaskQueueRequeued(int attempt, long pendingDepth, int activeLeases)
    {
        _ = attempt;
        _taskQueueRequeued?.Add(1);
        _taskQueuePending?.Add(1);
        _taskQueuePendingDepth?.Record(pendingDepth);
        _taskQueueActiveDepth?.Record(activeLeases);
    }

    internal static void RecordTaskQueueDeadLettered(int attempt, long pendingDepth, int activeLeases)
    {
        _ = attempt;
        _taskQueueDeadLettered?.Add(1);
        _taskQueueActiveDepth?.Record(activeLeases);
    }
}
