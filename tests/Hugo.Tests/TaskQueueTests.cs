using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests;

[Collection("TaskQueueConcurrency")]
internal class TaskQueueTests
{
    [Fact]
    public void TaskQueueOptions_InvalidCapacity_ShouldThrow() => Assert.Throws<ArgumentOutOfRangeException>(static () => new TaskQueueOptions { Capacity = 0 });

    [Fact]
    public void TaskQueueOptions_InvalidLeaseDuration_ShouldThrow() => Assert.Throws<ArgumentOutOfRangeException>(static () => new TaskQueueOptions { LeaseDuration = TimeSpan.Zero });

    [Fact]
    public void TaskQueueOptions_NegativeRequeueDelay_ShouldThrow() => Assert.Throws<ArgumentOutOfRangeException>(static () => new TaskQueueOptions { RequeueDelay = TimeSpan.FromMilliseconds(-1) });

    [Fact]
    public async Task EnqueueLeaseComplete_ShouldClearCounts()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<string>(timeProvider: provider);

        await queue.EnqueueAsync("alpha", TestContext.Current.CancellationToken);
        Assert.Equal(1, queue.PendingCount);

        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.Equal("alpha", lease.Value);
        Assert.Equal(1, lease.Attempt);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(1, queue.ActiveLeaseCount);

        await lease.CompleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, queue.ActiveLeaseCount);
    }

    [Fact]
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
        Assert.Equal(2, secondLease.Attempt);
        Assert.Equal("beta", secondLease.Value);
        Assert.Same(error, secondLease.LastError);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(1, queue.ActiveLeaseCount);

        await secondLease.CompleteAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
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

        Assert.Single(contexts);
        var context = contexts[0];
        Assert.Equal("gamma", context.Value);
        Assert.Equal(error, context.Error);
        Assert.Equal(lease.EnqueuedAt, context.EnqueuedAt);
        Assert.Equal(1, context.Attempt);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, queue.ActiveLeaseCount);
    }

    [Fact]
    public async Task FailAsync_ShouldThrowWhenErrorNull()
    {
        await using var queue = new TaskQueue<string>();

        await queue.EnqueueAsync("value", TestContext.Current.CancellationToken);
        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentNullException>(async () => await lease.FailAsync(null!, requeue: true, TestContext.Current.CancellationToken));
    }

    [Fact]
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
        Assert.Equal(2, secondLease.Attempt);
        Assert.Equal("delta", secondLease.Value);
        Assert.NotNull(secondLease.LastError);
        Assert.Equal(ErrorCodes.TaskQueueLeaseExpired, secondLease.LastError?.Code);
        Assert.True(secondLease.LastError!.TryGetMetadata<int>("attempt", out var attemptMetadata));
        Assert.Equal(1, attemptMetadata);
        Assert.True(secondLease.LastError!.TryGetMetadata<DateTimeOffset>("expiredAt", out var expiredAt));
        Assert.True(expiredAt >= firstLease.EnqueuedAt);
        Assert.Equal(firstLease.EnqueuedAt, secondLease.EnqueuedAt);
        Assert.True(secondLease.LastError!.TryGetMetadata<DateTimeOffset>("enqueuedAt", out var enqueuedAt));
        Assert.Equal(firstLease.EnqueuedAt, enqueuedAt);

        await secondLease.CompleteAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
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
        Assert.Single(contexts);
        Assert.Equal("epsilon", context.Value);
        Assert.Equal(2, context.Attempt);
        Assert.Equal(ErrorCodes.TaskQueueLeaseExpired, context.Error.Code);
        Assert.Equal(firstLease.EnqueuedAt, context.EnqueuedAt);
        Assert.True(context.Error.TryGetMetadata<int>("attempt", out var attemptMetadata));
        Assert.Equal(2, attemptMetadata);
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, queue.ActiveLeaseCount);
    }

    [Fact]
    public async Task LeaseOperations_ShouldRejectAfterCompletion()
    {
        await using var queue = new TaskQueue<string>();

        await queue.EnqueueAsync("theta", TestContext.Current.CancellationToken);
        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        await lease.CompleteAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.CompleteAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.HeartbeatAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await lease.FailAsync(Error.From("after-complete", ErrorCodes.TaskQueueAbandoned), cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
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
        canceledCts.Cancel();

        await lease.FailAsync(error, requeue: true, canceledCts.Token);

        var requeuedTask = queue.LeaseAsync(TestContext.Current.CancellationToken).AsTask();
        var requeued = await requeuedTask.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, requeued.Attempt);
        Assert.Equal("omega", requeued.Value);
        Assert.Same(error, requeued.LastError);
    }

    [Fact]
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

        var context = Assert.Single(contexts);
        Assert.Equal("sigma", context.Value);
        Assert.True(context.Attempt >= 2);
    }

    [Fact]
    public async Task TaskQueue_Disposed_ShouldThrowOnEnqueueAndLease()
    {
        await using var queue = new TaskQueue<string>();

        await queue.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await queue.EnqueueAsync("value", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await queue.LeaseAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
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

        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, queue.ActiveLeaseCount);
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
