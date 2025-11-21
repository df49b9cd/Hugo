using Microsoft.Extensions.Time.Testing;


namespace Hugo.Tests;

public sealed class WorkflowExecutionContextTests : IDisposable
{
    public WorkflowExecutionContextTests()
    {
    }

    public void Dispose()
    {
    }

    [Fact(Timeout = 15_000)]
    public void Tick_ShouldIncrementLogicalClock()
    {
        var context = CreateContext();

        context.LogicalClock.ShouldBe(0);
        context.Tick().ShouldBe(1);
        context.LogicalClock.ShouldBe(1);
        context.Tick().ShouldBe(2);
        context.LogicalClock.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public void Observe_ShouldAdvanceToObservedValue()
    {
        var context = CreateContext(initialLogicalClock: 5);

        var result = context.Observe(10);

        result.ShouldBe(11);
        context.LogicalClock.ShouldBe(11);
    }

    [Fact(Timeout = 15_000)]
    public void Observe_WithLowerValue_ShouldStillIncrement()
    {
        var context = CreateContext(initialLogicalClock: 5);

        var result = context.Observe(2);

        result.ShouldBe(6);
        context.LogicalClock.ShouldBe(6);
    }

    [Fact(Timeout = 15_000)]
    public void Observe_WithNegativeValue_ShouldThrow()
    {
        var context = CreateContext();

        Should.Throw<ArgumentOutOfRangeException>(() => context.Observe(-1));
    }

    [Fact(Timeout = 15_000)]
    public void Constructor_WithNegativeLogicalClock_ShouldThrow() => Should.Throw<ArgumentOutOfRangeException>(static () => CreateContext(initialLogicalClock: -1));

    [Fact(Timeout = 15_000)]
    public void Constructor_WithNegativeReplayCount_ShouldThrow() => Should.Throw<ArgumentOutOfRangeException>(static () => CreateContext(replayCount: -1));

    [Fact(Timeout = 15_000)]
    public void Constructor_WithEmptyNamespace_ShouldThrow() => Should.Throw<ArgumentException>(static () => new WorkflowExecutionContext(
                                                                         string.Empty,
                                                                         "workflow",
                                                                         "run",
                                                                         "queue"));

    [Fact(Timeout = 15_000)]
    public void Constructor_WithInvalidMetadataKey_ShouldThrow()
    {
        var metadata = new Dictionary<string, string> { [" "] = "value" };

        Should.Throw<ArgumentException>(() => CreateContext(metadata: metadata));
    }

    [Fact(Timeout = 15_000)]
    public void SnapshotVisibility_BeforeCompletion_ShouldReportActive()
    {
        var metadata = new Dictionary<string, string> { ["region"] = "us-east" };
        var context = CreateContext(metadata: metadata);

        using var scope = WorkflowExecution.Enter(context, cancellationToken: TestContext.Current.CancellationToken);
        var snapshot = context.SnapshotVisibility();

        snapshot.Status.ShouldBe(WorkflowStatus.Active);
        snapshot.CompletedAt.ShouldBeNull();
        snapshot.Attributes["region"].ShouldBe("us-east");
    }

    [Fact(Timeout = 15_000)]
    public void TryGetMetadata_ShouldReturnExistingValue()
    {
        var context = CreateContext(metadata: new Dictionary<string, string> { ["region"] = "eu-west" });

        context.TryGetMetadata("region", out var value).ShouldBeTrue();
        value.ShouldBe("eu-west");
        context.TryGetMetadata("missing", out _).ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public void Complete_ShouldPopulateVisibilityRecordAndErrorMetadata()
    {
        var provider = new FakeTimeProvider();
        var metadata = new Dictionary<string, string> { ["tenant"] = "contoso" };
        var context = CreateContext(provider: provider, metadata: metadata);

        using var scope = WorkflowExecution.Enter(context, cancellationToken: TestContext.Current.CancellationToken);
        provider.Advance(TimeSpan.FromSeconds(5));

        var error = Error.From("boom", "error.workflow");
        var record = context.Complete(WorkflowCompletionStatus.Failed, error);

        context.CompletionStatus.ShouldBe(WorkflowCompletionStatus.Failed);
        record.Status.ShouldBe(WorkflowStatus.Failed);
        record.CompletedAt.ShouldBe(context.StartedAt + TimeSpan.FromSeconds(5));
        record.Attributes["tenant"].ShouldBe("contoso");
        record.Attributes["error.code"].ShouldBe("error.workflow");
        context.CompletionError.ShouldNotBeNull();
        context.CompletionError!.TryGetMetadata("workflow.namespace", out string? ns).ShouldBeTrue();
        ns.ShouldBe(context.Namespace);
        context.CompletionError!.TryGetMetadata("workflow.metadata.tenant", out string? tenant).ShouldBeTrue();
        tenant.ShouldBe("contoso");
    }

    [Fact(Timeout = 15_000)]
    public void Complete_WithAttributes_ShouldMergeMetadata()
    {
        var provider = new FakeTimeProvider();
        var metadata = new Dictionary<string, string> { ["tenant"] = "contoso" };
        var context = CreateContext(provider: provider, metadata: metadata, scheduleId: "sched-1", scheduleGroup: "group-1");

        using var scope = WorkflowExecution.Enter(context, cancellationToken: TestContext.Current.CancellationToken);
        provider.Advance(TimeSpan.FromSeconds(1));

        var attributes = new Dictionary<string, string>
        {
            ["tenant"] = "override",
            ["custom"] = "value"
        };

        var record = context.Complete(WorkflowCompletionStatus.Canceled, attributes: attributes);

        record.Status.ShouldBe(WorkflowStatus.Canceled);
        record.Attributes["tenant"].ShouldBe("override");
        record.Attributes["custom"].ShouldBe("value");
        record.ScheduleId.ShouldBe("sched-1");
        record.ScheduleGroup.ShouldBe("group-1");
    }

    [Fact(Timeout = 15_000)]
    public void TryComplete_ShouldPreventDoubleCompletion()
    {
        var context = CreateContext();

        using var scope = WorkflowExecution.Enter(context, cancellationToken: TestContext.Current.CancellationToken);

        var record = context.Complete(WorkflowCompletionStatus.Completed);

        record.Status.ShouldBe(WorkflowStatus.Completed);
        Should.Throw<InvalidOperationException>(() => context.Complete(WorkflowCompletionStatus.Completed));
    }

    [Fact(Timeout = 15_000)]
    public void SnapshotVisibility_AfterCompletion_ShouldReportFinalState()
    {
        var provider = new FakeTimeProvider();
        var context = CreateContext(provider: provider);

        using var scope = WorkflowExecution.Enter(context, cancellationToken: TestContext.Current.CancellationToken);
        provider.Advance(TimeSpan.FromSeconds(2));
        context.Complete(WorkflowCompletionStatus.Completed);

        var snapshot = context.SnapshotVisibility();

        snapshot.Status.ShouldBe(WorkflowStatus.Completed);
        snapshot.CompletedAt.ShouldNotBeNull();
    }

    [Fact(Timeout = 15_000)]
    public void ResetLogicalClock_ShouldUpdateValue()
    {
        var context = CreateContext(initialLogicalClock: 2);

        context.ResetLogicalClock(10);

        context.LogicalClock.ShouldBe(10);
    }

    [Fact(Timeout = 15_000)]
    public void ResetLogicalClock_WithNegativeValue_ShouldThrow()
    {
        var context = CreateContext();

        Should.Throw<ArgumentOutOfRangeException>(() => context.ResetLogicalClock(-5));
    }

    [Fact(Timeout = 15_000)]
    public void IncrementReplayCount_ShouldUpdateProperty()
    {
        var context = CreateContext(replayCount: 1);

        var updated = context.IncrementReplayCount();

        updated.ShouldBe(2);
        context.ReplayCount.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public void Scope_ShouldManageAmbientContextLifecycle()
    {
        var context = CreateContext();

        WorkflowExecution.HasCurrent.ShouldBeFalse();
        WorkflowExecution.Current.ShouldBeNull();

        WorkflowExecutionContext? inner;
        using (var scope = WorkflowExecution.Enter(context, cancellationToken: TestContext.Current.CancellationToken))
        {
            inner = WorkflowExecution.Current;
            WorkflowExecution.HasCurrent.ShouldBeTrue();
            inner.ShouldBeSameAs(context);
            WorkflowExecution.RequireCurrent().ShouldBeSameAs(context);
        }

        WorkflowExecution.HasCurrent.ShouldBeFalse();
        WorkflowExecution.Current.ShouldBeNull();
    }

    [Fact(Timeout = 15_000)]
    public void RequireCurrent_WhenMissing_ShouldThrow() => Should.Throw<InvalidOperationException>(static () => WorkflowExecution.RequireCurrent());

    [Fact(Timeout = 15_000)]
    public void Enter_WithNullContext_ShouldThrow() => Should.Throw<ArgumentNullException>(static () => WorkflowExecution.Enter(null!, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public void CurrentCancellationToken_ShouldFlowThroughAmbientScope()
    {
        var context = CreateContext();
        using var cts = new CancellationTokenSource();

        using var scope = WorkflowExecution.Enter(context, cancellationToken: cts.Token);

        WorkflowExecution.HasCurrent.ShouldBeTrue();
        WorkflowExecution.CurrentCancellationToken.ShouldBe(cts.Token);
    }

    [Fact(Timeout = 15_000)]
    public void Scope_Complete_ShouldDelegateToContext()
    {
        var context = CreateContext();

        using var scope = WorkflowExecution.Enter(context, cancellationToken: TestContext.Current.CancellationToken);
        var record = scope.Complete(WorkflowCompletionStatus.Completed);

        record.Status.ShouldBe(WorkflowStatus.Completed);
        context.CompletionStatus.ShouldBe(WorkflowCompletionStatus.Completed);
    }

    [Fact(Timeout = 15_000)]
    public void Scope_TryComplete_ShouldReturnFalseAfterCompletion()
    {
        var context = CreateContext();

        using var scope = WorkflowExecution.Enter(context, cancellationToken: TestContext.Current.CancellationToken);

        scope.TryComplete(WorkflowCompletionStatus.Completed, null, null, out _).ShouldBeTrue();
        scope.TryComplete(WorkflowCompletionStatus.Failed, Error.From("boom", "error"), null, out _).ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public void Scope_DisposeOutOfOrder_ShouldThrow()
    {
        var outerContext = CreateContext(workflowId: "outer");
        var innerContext = CreateContext(workflowId: "inner");

        var outer = WorkflowExecution.Enter(outerContext, cancellationToken: TestContext.Current.CancellationToken);
        var inner = WorkflowExecution.Enter(innerContext, cancellationToken: TestContext.Current.CancellationToken);

        Should.Throw<InvalidOperationException>(() => outer.Dispose());
        WorkflowExecution.RequireCurrent().ShouldBeSameAs(innerContext);

        inner.Dispose();
        WorkflowExecution.RequireCurrent().ShouldBeSameAs(outerContext);

        var cleanup = WorkflowExecution.Enter(CreateContext(workflowId: "cleanup"), replace: true, cancellationToken: TestContext.Current.CancellationToken);
        cleanup.Dispose();

        WorkflowExecution.HasCurrent.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public void Enter_WithReplace_ShouldSwapAmbientContext()
    {
        var original = CreateContext(workflowId: "original");
        var replacement = CreateContext(workflowId: "replacement");

        var originalScope = WorkflowExecution.Enter(original, cancellationToken: TestContext.Current.CancellationToken);

        using (WorkflowExecution.Enter(replacement, replace: true, cancellationToken: TestContext.Current.CancellationToken))
        {
            WorkflowExecution.RequireCurrent().ShouldBeSameAs(replacement);
        }

        WorkflowExecution.HasCurrent.ShouldBeFalse();
        Should.Throw<InvalidOperationException>(() => originalScope.Dispose());
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Scope_DisposeAsync_ShouldClearAmbientContext()
    {
        var context = CreateContext();

        var scope = WorkflowExecution.Enter(context, cancellationToken: TestContext.Current.CancellationToken);
        await scope.DisposeAsync();

        WorkflowExecution.HasCurrent.ShouldBeFalse();
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
