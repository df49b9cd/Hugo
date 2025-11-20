using Microsoft.Extensions.Time.Testing;

using Shouldly;

namespace Hugo.Tests;

[Collection("TaskQueueConcurrency")]
public class TaskQueueTests
{
    [Fact(Timeout = 15_000)]
    public void TaskQueueOptions_InvalidCapacity_ShouldThrow() => Should.Throw<ArgumentOutOfRangeException>(static () => new TaskQueueOptions { Capacity = 0 });

    [Fact(Timeout = 15_000)]
    public void TaskQueueOptions_InvalidLeaseDuration_ShouldThrow() => Should.Throw<ArgumentOutOfRangeException>(static () => new TaskQueueOptions { LeaseDuration = TimeSpan.Zero });

    [Fact(Timeout = 15_000)]
    public void TaskQueueOptions_NegativeRequeueDelay_ShouldThrow() => Should.Throw<ArgumentOutOfRangeException>(static () => new TaskQueueOptions { RequeueDelay = TimeSpan.FromMilliseconds(-1) });

    [Fact(Timeout = 5_000)]
    public async Task EnqueueAsync_WithAvailableCapacity_CompletesSynchronously()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<string>(new TaskQueueOptions { Capacity = 8 }, provider);

        ValueTask enqueue = queue.EnqueueAsync("fast", TestContext.Current.CancellationToken);
        enqueue.IsCompletedSuccessfully.ShouldBeTrue();
        await enqueue;

        queue.PendingCount.ShouldBe(1);

        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await lease.CompleteAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 5_000)]
    public async Task LeaseAsync_WhenItemBuffered_CompletesSynchronously()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<string>(new TaskQueueOptions { Capacity = 4 }, provider);

        await queue.EnqueueAsync("hot", TestContext.Current.CancellationToken);

        ValueTask<TaskQueueLease<string>> leaseTask = queue.LeaseAsync(TestContext.Current.CancellationToken);
        leaseTask.IsCompletedSuccessfully.ShouldBeTrue();

        var lease = await leaseTask;
        lease.Value.ShouldBe("hot");

        await lease.CompleteAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 15_000)]
    public async Task EnqueueLeaseComplete_ShouldClearCounts()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<string>(timeProvider: provider);

        await queue.EnqueueAsync("alpha", TestContext.Current.CancellationToken);
        queue.PendingCount.ShouldBe(1);

        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        lease.Value.ShouldBe("alpha");
        lease.Attempt.ShouldBe(1);
        queue.PendingCount.ShouldBe(0);
        queue.ActiveLeaseCount.ShouldBe(1);

        await lease.CompleteAsync(TestContext.Current.CancellationToken);

        queue.PendingCount.ShouldBe(0);
        queue.ActiveLeaseCount.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async Task FailAsync_WithRequeue_ShouldIncrementAttempt()
    {
        var provider = new FakeTimeProvider();
        var options = new TaskQueueOptions { MaxDeliveryAttempts = 3 };
        await using var queue = new TaskQueue<string>(options, provider);

        await queue.EnqueueAsync("beta", TestContext.Current.CancellationToken);
        var firstLease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        var error = Error.From("worker abandoned", ErrorCodes.TaskQueueAbandoned)
            .WithMetadata("worker", "A");

        await firstLease.FailAsync(error, requeue: true, TestContext.Current.CancellationToken);

        var secondLease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        secondLease.Attempt.ShouldBe(2);
        secondLease.Value.ShouldBe("beta");
        secondLease.LastError.ShouldBeSameAs(error);
        queue.PendingCount.ShouldBe(0);
        queue.ActiveLeaseCount.ShouldBe(1);

        await secondLease.CompleteAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 15_000)]
    public async Task FailAsync_WithoutRequeue_ShouldDeadLetter()
    {
        var provider = new FakeTimeProvider();
        var contexts = new List<TaskQueueDeadLetterContext<string>>();

        ValueTask DeadLetter(TaskQueueDeadLetterContext<string> context, CancellationToken cancellationToken)
        {
            contexts.Add(context);
            return ValueTask.CompletedTask;
        }

        await using var queue = new TaskQueue<string>(timeProvider: provider, deadLetter: DeadLetter);

        await queue.EnqueueAsync("gamma", TestContext.Current.CancellationToken);
        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        var error = Error.From("fatal", ErrorCodes.TaskQueueAbandoned);

        await lease.FailAsync(error, requeue: false, TestContext.Current.CancellationToken);

        contexts.ShouldHaveSingleItem();
        var context = contexts[0];
        context.Value.ShouldBe("gamma");
        context.Error.ShouldBe(error);
        context.EnqueuedAt.ShouldBe(lease.EnqueuedAt);
        context.Attempt.ShouldBe(1);
        queue.PendingCount.ShouldBe(0);
        queue.ActiveLeaseCount.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async Task FailAsync_ShouldThrowWhenErrorNull()
    {
        await using var queue = new TaskQueue<string>();

        await queue.EnqueueAsync("value", TestContext.Current.CancellationToken);
        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<ArgumentNullException>(async () => await lease.FailAsync(null!, requeue: true, TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task LeaseExpiration_ShouldRequeueWithExpiredError()
    {
        var provider = new FakeTimeProvider();
        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(50),
            LeaseSweepInterval = TimeSpan.FromMilliseconds(20),
            HeartbeatInterval = TimeSpan.Zero,
            MaxDeliveryAttempts = 3
        };

        await using var queue = new TaskQueue<string>(options, provider);

        await queue.EnqueueAsync("delta", TestContext.Current.CancellationToken);
        var firstLease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        await AdvanceAsync(provider, TimeSpan.FromMilliseconds(70));
        var secondLeaseTask = queue.LeaseAsync(TestContext.Current.CancellationToken).AsTask();
        await AdvanceAsync(provider, TimeSpan.FromMilliseconds(40));
        var secondLease = await secondLeaseTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        secondLease.Attempt.ShouldBe(2);
        secondLease.Value.ShouldBe("delta");
        secondLease.LastError.ShouldNotBeNull();
        secondLease.LastError?.Code.ShouldBe(ErrorCodes.TaskQueueLeaseExpired);
        secondLease.LastError!.TryGetMetadata<int>("attempt", out var attemptMetadata).ShouldBeTrue();
        attemptMetadata.ShouldBe(1);
        secondLease.LastError!.TryGetMetadata<DateTimeOffset>("expiredAt", out var expiredAt).ShouldBeTrue();
        (expiredAt >= firstLease.EnqueuedAt).ShouldBeTrue();
        secondLease.EnqueuedAt.ShouldBe(firstLease.EnqueuedAt);
        secondLease.LastError!.TryGetMetadata<DateTimeOffset>("enqueuedAt", out var enqueuedAt).ShouldBeTrue();
        enqueuedAt.ShouldBe(firstLease.EnqueuedAt);

        await secondLease.CompleteAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 15_000)]
    public async Task LeaseExpiration_PastMaxAttempts_ShouldDeadLetter()
    {
        var provider = new FakeTimeProvider();
        var contexts = new List<TaskQueueDeadLetterContext<string>>();
        var deadLetterSignal = new TaskCompletionSource<TaskQueueDeadLetterContext<string>>(TaskCreationOptions.RunContinuationsAsynchronously);

        ValueTask DeadLetter(TaskQueueDeadLetterContext<string> context, CancellationToken cancellationToken)
        {
            contexts.Add(context);
            deadLetterSignal.TrySetResult(context);
            return ValueTask.CompletedTask;
        }

        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(60),
            LeaseSweepInterval = TimeSpan.FromMilliseconds(25),
            HeartbeatInterval = TimeSpan.Zero,
            MaxDeliveryAttempts = 2
        };

        await using var queue = new TaskQueue<string>(options, provider, DeadLetter);

        await queue.EnqueueAsync("epsilon", TestContext.Current.CancellationToken);
        var firstLease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        await AdvanceAsync(provider, TimeSpan.FromMilliseconds(80));
        var secondLeaseTask = queue.LeaseAsync(TestContext.Current.CancellationToken).AsTask();
        await AdvanceAsync(provider, TimeSpan.FromMilliseconds(50));
        var secondLease = await secondLeaseTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        await AdvanceAsync(provider, TimeSpan.FromMilliseconds(80));
        await AdvanceAsync(provider, TimeSpan.FromMilliseconds(50));

        var context = await deadLetterSignal.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        contexts.ShouldHaveSingleItem();
        context.Value.ShouldBe("epsilon");
        context.Attempt.ShouldBe(2);
        context.Error.Code.ShouldBe(ErrorCodes.TaskQueueLeaseExpired);
        context.EnqueuedAt.ShouldBe(firstLease.EnqueuedAt);
        context.Error.TryGetMetadata<int>("attempt", out var attemptMetadata).ShouldBeTrue();
        attemptMetadata.ShouldBe(2);
        queue.PendingCount.ShouldBe(0);
        queue.ActiveLeaseCount.ShouldBe(0);
    }

    [Fact(Timeout = 15_000)]
    public async Task LeaseOperations_ShouldRejectAfterCompletion()
    {
        await using var queue = new TaskQueue<string>();

        await queue.EnqueueAsync("theta", TestContext.Current.CancellationToken);
        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        await lease.CompleteAsync(TestContext.Current.CancellationToken);

        await Should.ThrowAsync<InvalidOperationException>(async () => await lease.CompleteAsync(TestContext.Current.CancellationToken));
        await Should.ThrowAsync<InvalidOperationException>(async () => await lease.HeartbeatAsync(TestContext.Current.CancellationToken));
        await Should.ThrowAsync<InvalidOperationException>(async () => await lease.FailAsync(Error.From("after-complete", ErrorCodes.TaskQueueAbandoned), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task FailAsync_WithCanceledToken_ShouldStillRequeue()
    {
        var provider = new FakeTimeProvider();
        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(50),
            LeaseSweepInterval = TimeSpan.FromMilliseconds(10),
            HeartbeatInterval = TimeSpan.Zero,
            MaxDeliveryAttempts = 3
        };

        await using var queue = new TaskQueue<string>(options, provider);

        await queue.EnqueueAsync("omega", TestContext.Current.CancellationToken);
        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        var error = Error.From("canceled-fail", ErrorCodes.TaskQueueAbandoned);
        using var canceledCts = new CancellationTokenSource();
        await canceledCts.CancelAsync();

        await lease.FailAsync(error, requeue: true, canceledCts.Token);

        var requeuedTask = queue.LeaseAsync(TestContext.Current.CancellationToken).AsTask();
        var requeued = await requeuedTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        requeued.Attempt.ShouldBe(2);
        requeued.Value.ShouldBe("omega");
        requeued.LastError.ShouldBeSameAs(error);
    }

    [Fact(Timeout = 15_000)]
    public async Task LeaseExpiration_DuringDispose_ShouldSurfaceDeadLetter()
    {
        var provider = new FakeTimeProvider();
        var contexts = new List<TaskQueueDeadLetterContext<string>>();

        ValueTask DeadLetter(TaskQueueDeadLetterContext<string> context, CancellationToken cancellationToken)
        {
            contexts.Add(context);
            return ValueTask.CompletedTask;
        }

        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromMilliseconds(50),
            LeaseSweepInterval = TimeSpan.FromMilliseconds(10),
            HeartbeatInterval = TimeSpan.Zero,
            MaxDeliveryAttempts = 2
        };

        await using var queue = new TaskQueue<string>(options, provider, DeadLetter);

        await queue.EnqueueAsync("sigma", TestContext.Current.CancellationToken);
        _ = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        await AdvanceAsync(provider, TimeSpan.FromMilliseconds(75));
        await AdvanceAsync(provider, TimeSpan.FromMilliseconds(75));

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        waitCts.CancelAfter(TimeSpan.FromSeconds(2));
        await WaitForConditionAsync(() => queue.ActiveLeaseCount == 0, waitCts.Token);

        await queue.DisposeAsync();

        var context = contexts.ShouldHaveSingleItem();
        context.Value.ShouldBe("sigma");
        (context.Attempt >= 2).ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task TaskQueue_Disposed_ShouldThrowOnEnqueueAndLease()
    {
        await using var queue = new TaskQueue<string>();

        await queue.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(async () => await queue.EnqueueAsync("value", TestContext.Current.CancellationToken));
        await Should.ThrowAsync<ObjectDisposedException>(async () => await queue.LeaseAsync(TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task OwnershipToken_ShouldAdvancePerLease()
    {
        await using var queue = new TaskQueue<string>();
        await queue.EnqueueAsync("value", TestContext.Current.CancellationToken);

        var firstLease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        TaskQueueOwnershipToken firstToken = firstLease.OwnershipToken;
        await firstLease.FailAsync(Error.From("retry", ErrorCodes.TaskQueueAbandoned), requeue: true, TestContext.Current.CancellationToken);

        var secondLease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        TaskQueueOwnershipToken secondToken = secondLease.OwnershipToken;

        secondToken.SequenceId.ShouldBe(firstToken.SequenceId);
        secondToken.Attempt.ShouldBe(firstToken.Attempt + 1);
        secondToken.LeaseId.ShouldNotBe(firstToken.LeaseId);
    }

    [Fact(Timeout = 15_000)]
    public async Task DrainAndRestore_ShouldRoundTripPendingItems()
    {
        await using var queue = new TaskQueue<string>();
        await queue.EnqueueAsync("alpha", TestContext.Current.CancellationToken);
        await queue.EnqueueAsync("bravo", TestContext.Current.CancellationToken);

        IReadOnlyList<TaskQueuePendingItem<string>> snapshot = await queue.DrainPendingItemsAsync(TestContext.Current.CancellationToken);
        snapshot.Count.ShouldBe(2);
        (snapshot[0].SequenceId < snapshot[1].SequenceId).ShouldBeTrue();

        await using var restored = new TaskQueue<string>();
        await restored.RestorePendingItemsAsync(snapshot, TestContext.Current.CancellationToken);

        restored.PendingCount.ShouldBe(snapshot.Count);
        var lease = await restored.LeaseAsync(TestContext.Current.CancellationToken);
        lease.Value.ShouldBe("alpha");
    }

    [Fact(Timeout = 15_000)]
    public async Task BackpressureCallbacks_ShouldFireOnThresholdTransitions()
    {
        var provider = new FakeTimeProvider();
        var notifications = new List<TaskQueueBackpressureState>();
        TaskQueueBackpressureOptions backpressure = new()
        {
            HighWatermark = 2,
            LowWatermark = 1,
            Cooldown = TimeSpan.FromMilliseconds(1),
            StateChanged = state => notifications.Add(state)
        };

        var options = new TaskQueueOptions
        {
            Capacity = 8,
            Backpressure = backpressure
        };

        await using var queue = new TaskQueue<int>(options, provider);
        await queue.EnqueueAsync(1, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(2, TestContext.Current.CancellationToken);

        provider.Advance(TimeSpan.FromMilliseconds(2));
        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        provider.Advance(TimeSpan.FromMilliseconds(2));
        await lease.CompleteAsync(TestContext.Current.CancellationToken);

        notifications.Count.ShouldBe(2);
        notifications[0].IsActive.ShouldBeTrue();
        notifications[1].IsActive.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task Heartbeat_ShouldExtendLease()
    {
        var provider = new FakeTimeProvider();
        var options = new TaskQueueOptions
        {
            LeaseDuration = TimeSpan.FromSeconds(5),
            LeaseSweepInterval = TimeSpan.FromSeconds(1),
            HeartbeatInterval = TimeSpan.FromSeconds(1)
        };

        await using var queue = new TaskQueue<string>(options, provider);

        await queue.EnqueueAsync("zeta", TestContext.Current.CancellationToken);
        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        provider.Advance(TimeSpan.FromSeconds(2));
        await lease.HeartbeatAsync(TestContext.Current.CancellationToken);

        provider.Advance(TimeSpan.FromSeconds(2));
        await lease.HeartbeatAsync(TestContext.Current.CancellationToken);

        provider.Advance(TimeSpan.FromSeconds(2));
        await lease.CompleteAsync(TestContext.Current.CancellationToken);

        provider.Advance(TimeSpan.FromSeconds(3));

        queue.PendingCount.ShouldBe(0);
        queue.ActiveLeaseCount.ShouldBe(0);
    }

    private static async Task AdvanceAsync(FakeTimeProvider provider, TimeSpan interval)
    {
        provider.Advance(interval);
        await Task.Yield();
    }

    private static async Task WaitForConditionAsync(Func<bool> condition, CancellationToken cancellationToken, TimeSpan? pollInterval = null)
    {
        var interval = pollInterval ?? TimeSpan.FromMilliseconds(10);
        var spin = new System.Threading.SpinWait();

        for (var i = 0; i < 128; i++)
        {
            if (condition())
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();
            spin.SpinOnce();
        }

        while (!condition())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(interval, cancellationToken);
        }
    }

}
