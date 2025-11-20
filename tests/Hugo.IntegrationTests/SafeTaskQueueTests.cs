using Microsoft.Extensions.Time.Testing;

using Shouldly;

namespace Hugo.Tests;

[Collection("TaskQueueConcurrency")]
public sealed class SafeTaskQueueTests
{
    [Fact(Timeout = 15_000)]
    public async Task EnqueueLeaseComplete_ShouldReturnSuccess()
    {
        await using var queue = new TaskQueue<string>();
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        var enqueue = await safeQueue.EnqueueAsync("alpha", TestContext.Current.CancellationToken);
        enqueue.IsSuccess.ShouldBeTrue();

        var leaseResult = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        leaseResult.IsSuccess.ShouldBeTrue();
        var lease = leaseResult.Value;
        lease.Value.ShouldBe("alpha");

        var complete = await lease.CompleteAsync(TestContext.Current.CancellationToken);
        complete.IsSuccess.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task Lease_WhenQueueDisposed_ShouldFail()
    {
        await using var queue = new TaskQueue<string>();
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        await queue.DisposeAsync();

        var enqueue = await safeQueue.EnqueueAsync("beta", TestContext.Current.CancellationToken);
        enqueue.IsFailure.ShouldBeTrue();
        enqueue.Error?.Code.ShouldBe(ErrorCodes.TaskQueueDisposed);

        var lease = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        lease.IsFailure.ShouldBeTrue();
        lease.Error?.Code.ShouldBe(ErrorCodes.TaskQueueDisposed);
    }

    [Fact(Timeout = 15_000)]
    public async Task LeaseOperations_ShouldReturnFailuresInsteadOfThrowing()
    {
        await using var queue = new TaskQueue<string>();
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        await safeQueue.EnqueueAsync("gamma", TestContext.Current.CancellationToken);
        var leaseResult = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        leaseResult.IsSuccess.ShouldBeTrue();
        var lease = leaseResult.Value;

        var completed = await lease.CompleteAsync(TestContext.Current.CancellationToken);
        completed.IsSuccess.ShouldBeTrue();

        var secondComplete = await lease.CompleteAsync(TestContext.Current.CancellationToken);
        secondComplete.IsFailure.ShouldBeTrue();
        secondComplete.Error?.Code.ShouldBe(ErrorCodes.TaskQueueLeaseInactive);

        var heartbeat = await lease.HeartbeatAsync(TestContext.Current.CancellationToken);
        heartbeat.IsFailure.ShouldBeTrue();
        heartbeat.Error?.Code.ShouldBe(ErrorCodes.TaskQueueLeaseInactive);

        var fail = await lease.FailAsync(Error.From("late fail", ErrorCodes.TaskQueueAbandoned), cancellationToken: TestContext.Current.CancellationToken);
        fail.IsFailure.ShouldBeTrue();
        fail.Error?.Code.ShouldBe(ErrorCodes.TaskQueueLeaseInactive);
    }

    [Fact(Timeout = 15_000)]
    public async Task FailAsync_WithNullError_ShouldReturnValidationError()
    {
        await using var queue = new TaskQueue<string>();
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        await safeQueue.EnqueueAsync("delta", TestContext.Current.CancellationToken);
        var leaseResult = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        leaseResult.IsSuccess.ShouldBeTrue();
        var lease = leaseResult.Value;

        var result = await lease.FailAsync(null, cancellationToken: TestContext.Current.CancellationToken);
        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }

    [Fact(Timeout = 15_000)]
    public async Task FailAsync_WithRequeue_ShouldReturnSuccess()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<string>(timeProvider: provider);
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        await safeQueue.EnqueueAsync("epsilon", TestContext.Current.CancellationToken);
        var leaseResult = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        leaseResult.IsSuccess.ShouldBeTrue();
        var lease = leaseResult.Value;
        var error = Error.From("retry", ErrorCodes.TaskQueueAbandoned);

        var fail = await lease.FailAsync(error, requeue: true, TestContext.Current.CancellationToken);
        fail.IsSuccess.ShouldBeTrue();

        var nextLeaseResult = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        nextLeaseResult.IsSuccess.ShouldBeTrue();
        var nextLease = nextLeaseResult.Value;
        nextLease.Attempt.ShouldBe(2);
        nextLease.LastError.ShouldBeSameAs(error);
    }

    [Fact(Timeout = 15_000)]
    public async Task LeaseAsync_WhenCanceled_ShouldReturnCanceledError()
    {
        await using var queue = new TaskQueue<string>();
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await safeQueue.LeaseAsync(cts.Token);
        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }
}
