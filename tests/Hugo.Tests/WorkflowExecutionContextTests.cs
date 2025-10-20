using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Hugo.Tests;

public sealed class WorkflowExecutionContextTests : IDisposable
{
    public WorkflowExecutionContextTests()
    {
        GoDiagnostics.Reset();
    }

    public void Dispose()
    {
        GoDiagnostics.Reset();
    }

    [Fact]
    public void Tick_ShouldIncrementLogicalClock()
    {
        var context = CreateContext();

        Assert.Equal(0, context.LogicalClock);
        Assert.Equal(1, context.Tick());
        Assert.Equal(1, context.LogicalClock);
        Assert.Equal(2, context.Tick());
        Assert.Equal(2, context.LogicalClock);
    }

    [Fact]
    public void Observe_ShouldAdvanceToObservedValue()
    {
        var context = CreateContext(initialLogicalClock: 5);

        var result = context.Observe(10);

        Assert.Equal(11, result);
        Assert.Equal(11, context.LogicalClock);
    }

    [Fact]
    public void Observe_WithLowerValue_ShouldStillIncrement()
    {
        var context = CreateContext(initialLogicalClock: 5);

        var result = context.Observe(2);

        Assert.Equal(6, result);
        Assert.Equal(6, context.LogicalClock);
    }

    [Fact]
    public void Observe_WithNegativeValue_ShouldThrow()
    {
        var context = CreateContext();

        Assert.Throws<ArgumentOutOfRangeException>(() => context.Observe(-1));
    }

    [Fact]
    public void Constructor_WithNegativeLogicalClock_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateContext(initialLogicalClock: -1));
    }

    [Fact]
    public void Constructor_WithNegativeReplayCount_ShouldThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateContext(replayCount: -1));
    }

    [Fact]
    public void Constructor_WithEmptyNamespace_ShouldThrow()
    {
        Assert.Throws<ArgumentException>(() => new WorkflowExecutionContext(
            string.Empty,
            "workflow",
            "run",
            "queue"));
    }

    [Fact]
    public void Constructor_WithInvalidMetadataKey_ShouldThrow()
    {
        var metadata = new Dictionary<string, string> { [" "] = "value" };

        Assert.Throws<ArgumentException>(() => CreateContext(metadata: metadata));
    }

    [Fact]
    public void SnapshotVisibility_BeforeCompletion_ShouldReportActive()
    {
        var metadata = new Dictionary<string, string> { ["region"] = "us-east" };
        var context = CreateContext(metadata: metadata);

        using var scope = WorkflowExecution.Enter(context, TestContext.Current.CancellationToken);
        var snapshot = context.SnapshotVisibility();

        Assert.Equal(WorkflowStatus.Active, snapshot.Status);
        Assert.Null(snapshot.CompletedAt);
        Assert.Equal("us-east", snapshot.Attributes["region"]);
    }

    [Fact]
    public void Complete_ShouldPopulateVisibilityRecordAndErrorMetadata()
    {
        var provider = new FakeTimeProvider();
        var metadata = new Dictionary<string, string> { ["tenant"] = "contoso" };
        var context = CreateContext(provider: provider, metadata: metadata);

        using var scope = WorkflowExecution.Enter(context, TestContext.Current.CancellationToken);
        provider.Advance(TimeSpan.FromSeconds(5));

        var error = Error.From("boom", "error.workflow");
        var record = context.Complete(WorkflowCompletionStatus.Failed, error);

        Assert.Equal(WorkflowCompletionStatus.Failed, context.CompletionStatus);
        Assert.Equal(WorkflowStatus.Failed, record.Status);
        Assert.Equal(context.StartedAt + TimeSpan.FromSeconds(5), record.CompletedAt);
        Assert.Equal("contoso", record.Attributes["tenant"]);
        Assert.Equal("error.workflow", record.Attributes["error.code"]);
        Assert.NotNull(context.CompletionError);
        Assert.True(context.CompletionError!.TryGetMetadata("workflow.namespace", out string? ns));
        Assert.Equal(context.Namespace, ns);
    }

    [Fact]
    public void Complete_WithAttributes_ShouldMergeMetadata()
    {
        var provider = new FakeTimeProvider();
        var metadata = new Dictionary<string, string> { ["tenant"] = "contoso" };
        var context = CreateContext(provider: provider, metadata: metadata, scheduleId: "sched-1", scheduleGroup: "group-1");

        using var scope = WorkflowExecution.Enter(context, TestContext.Current.CancellationToken);
        provider.Advance(TimeSpan.FromSeconds(1));

        var attributes = new Dictionary<string, string>
        {
            ["tenant"] = "override",
            ["custom"] = "value"
        };

        var record = context.Complete(WorkflowCompletionStatus.Canceled, attributes: attributes);

        Assert.Equal(WorkflowStatus.Canceled, record.Status);
        Assert.Equal("override", record.Attributes["tenant"]);
        Assert.Equal("value", record.Attributes["custom"]);
        Assert.Equal("sched-1", record.ScheduleId);
        Assert.Equal("group-1", record.ScheduleGroup);
    }

    [Fact]
    public void TryComplete_ShouldPreventDoubleCompletion()
    {
        var context = CreateContext();

        using var scope = WorkflowExecution.Enter(context, TestContext.Current.CancellationToken);

        var record = context.Complete(WorkflowCompletionStatus.Completed);

        Assert.Equal(WorkflowStatus.Completed, record.Status);
        Assert.Throws<InvalidOperationException>(() => context.Complete(WorkflowCompletionStatus.Completed));
    }

    [Fact]
    public void SnapshotVisibility_AfterCompletion_ShouldReportFinalState()
    {
        var provider = new FakeTimeProvider();
        var context = CreateContext(provider: provider);

        using var scope = WorkflowExecution.Enter(context, TestContext.Current.CancellationToken);
        provider.Advance(TimeSpan.FromSeconds(2));
        context.Complete(WorkflowCompletionStatus.Completed);

        var snapshot = context.SnapshotVisibility();

        Assert.Equal(WorkflowStatus.Completed, snapshot.Status);
        Assert.NotNull(snapshot.CompletedAt);
    }

    [Fact]
    public void ResetLogicalClock_ShouldUpdateValue()
    {
        var context = CreateContext(initialLogicalClock: 2);

        context.ResetLogicalClock(10);

        Assert.Equal(10, context.LogicalClock);
    }

    [Fact]
    public void ResetLogicalClock_WithNegativeValue_ShouldThrow()
    {
        var context = CreateContext();

        Assert.Throws<ArgumentOutOfRangeException>(() => context.ResetLogicalClock(-5));
    }

    [Fact]
    public void IncrementReplayCount_ShouldUpdateProperty()
    {
        var context = CreateContext(replayCount: 1);

        var updated = context.IncrementReplayCount();

        Assert.Equal(2, updated);
        Assert.Equal(2, context.ReplayCount);
    }

    [Fact]
    public void Scope_ShouldManageAmbientContextLifecycle()
    {
        var context = CreateContext();

        Assert.False(WorkflowExecution.HasCurrent);
        Assert.Null(WorkflowExecution.Current);

        WorkflowExecutionContext? inner;
        using (var scope = WorkflowExecution.Enter(context, TestContext.Current.CancellationToken))
        {
            inner = WorkflowExecution.Current;
            Assert.True(WorkflowExecution.HasCurrent);
            Assert.Same(context, inner);
            Assert.Same(context, WorkflowExecution.RequireCurrent());
        }

        Assert.False(WorkflowExecution.HasCurrent);
        Assert.Null(WorkflowExecution.Current);
    }

    [Fact]
    public void RequireCurrent_WhenMissing_ShouldThrow()
    {
        Assert.Throws<InvalidOperationException>(() => WorkflowExecution.RequireCurrent());
    }

    [Fact]
    public void Enter_WithNullContext_ShouldThrow()
    {
        Assert.Throws<ArgumentNullException>(() => WorkflowExecution.Enter(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void CurrentCancellationToken_ShouldFlowThroughAmbientScope()
    {
        var context = CreateContext();
        using var cts = new CancellationTokenSource();

        using var scope = WorkflowExecution.Enter(context, cts.Token);

        Assert.True(WorkflowExecution.HasCurrent);
        Assert.Equal(cts.Token, WorkflowExecution.CurrentCancellationToken);
    }

    [Fact]
    public void Scope_Complete_ShouldDelegateToContext()
    {
        var context = CreateContext();

        using var scope = WorkflowExecution.Enter(context, TestContext.Current.CancellationToken);
        var record = scope.Complete(WorkflowCompletionStatus.Completed);

        Assert.Equal(WorkflowStatus.Completed, record.Status);
        Assert.Equal(WorkflowCompletionStatus.Completed, context.CompletionStatus);
    }

    [Fact]
    public void Scope_TryComplete_ShouldReturnFalseAfterCompletion()
    {
        var context = CreateContext();

        using var scope = WorkflowExecution.Enter(context, TestContext.Current.CancellationToken);

        Assert.True(scope.TryComplete(WorkflowCompletionStatus.Completed, null, null, out _));
        Assert.False(scope.TryComplete(WorkflowCompletionStatus.Failed, Error.From("boom", "error"), null, out _));
    }

    [Fact]
    public void Scope_DisposeOutOfOrder_ShouldThrow()
    {
        var outerContext = CreateContext(workflowId: "outer");
        var innerContext = CreateContext(workflowId: "inner");

        var outer = WorkflowExecution.Enter(outerContext, TestContext.Current.CancellationToken);
        var inner = WorkflowExecution.Enter(innerContext, TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => outer.Dispose());
        Assert.Same(innerContext, WorkflowExecution.RequireCurrent());

        inner.Dispose();
        Assert.Same(outerContext, WorkflowExecution.RequireCurrent());

        var cleanup = WorkflowExecution.Enter(CreateContext(workflowId: "cleanup"), TestContext.Current.CancellationToken, replace: true);
        cleanup.Dispose();

        Assert.False(WorkflowExecution.HasCurrent);
    }

    [Fact]
    public async Task Scope_DisposeAsync_ShouldClearAmbientContext()
    {
        var context = CreateContext();

        var scope = WorkflowExecution.Enter(context, TestContext.Current.CancellationToken);
        await scope.DisposeAsync();

        Assert.False(WorkflowExecution.HasCurrent);
    }

    private static WorkflowExecutionContext CreateContext(
        string? workflowId = null,
        FakeTimeProvider? provider = null,
        IDictionary<string, string>? metadata = null,
        long initialLogicalClock = 0,
        int replayCount = 0,
        string? scheduleId = null,
        string? scheduleGroup = null)
    {
        provider ??= new FakeTimeProvider();
        workflowId ??= "workflow-1";

        return new WorkflowExecutionContext(
            "default",
            workflowId,
            "run-1",
            "queue-1",
            scheduleId,
            scheduleGroup,
            metadata: metadata ?? new Dictionary<string, string>(),
            timeProvider: provider,
            initialLogicalClock: initialLogicalClock,
            replayCount: replayCount);
    }
}
