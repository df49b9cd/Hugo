using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests;

[Collection("TaskQueueConcurrency")]
public sealed class SafeTaskQueueTests
{
    [Fact]
    public async Task EnqueueLeaseComplete_ShouldReturnSuccess()
    {
        await using var queue = new TaskQueue<string>();
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        var enqueue = await safeQueue.EnqueueAsync("alpha", TestContext.Current.CancellationToken);
        Assert.True(enqueue.IsSuccess);

        var leaseResult = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.True(leaseResult.IsSuccess);
        var lease = leaseResult.Value;
        Assert.Equal("alpha", lease.Value);

        var complete = await lease.CompleteAsync(TestContext.Current.CancellationToken);
        Assert.True(complete.IsSuccess);
    }

    [Fact]
    public async Task Lease_WhenQueueDisposed_ShouldFail()
    {
        await using var queue = new TaskQueue<string>();
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        await queue.DisposeAsync();

        var enqueue = await safeQueue.EnqueueAsync("beta", TestContext.Current.CancellationToken);
        Assert.True(enqueue.IsFailure);
        Assert.Equal(ErrorCodes.TaskQueueDisposed, enqueue.Error?.Code);

        var lease = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.True(lease.IsFailure);
        Assert.Equal(ErrorCodes.TaskQueueDisposed, lease.Error?.Code);
    }

    [Fact]
    public async Task LeaseOperations_ShouldReturnFailuresInsteadOfThrowing()
    {
        await using var queue = new TaskQueue<string>();
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        await safeQueue.EnqueueAsync("gamma", TestContext.Current.CancellationToken);
        var leaseResult = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.True(leaseResult.IsSuccess);
        var lease = leaseResult.Value;

        var completed = await lease.CompleteAsync(TestContext.Current.CancellationToken);
        Assert.True(completed.IsSuccess);

        var secondComplete = await lease.CompleteAsync(TestContext.Current.CancellationToken);
        Assert.True(secondComplete.IsFailure);
        Assert.Equal(ErrorCodes.TaskQueueLeaseInactive, secondComplete.Error?.Code);

        var heartbeat = await lease.HeartbeatAsync(TestContext.Current.CancellationToken);
        Assert.True(heartbeat.IsFailure);
        Assert.Equal(ErrorCodes.TaskQueueLeaseInactive, heartbeat.Error?.Code);

        var fail = await lease.FailAsync(Error.From("late fail", ErrorCodes.TaskQueueAbandoned), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(fail.IsFailure);
        Assert.Equal(ErrorCodes.TaskQueueLeaseInactive, fail.Error?.Code);
    }

    [Fact]
    public async Task FailAsync_WithNullError_ShouldReturnValidationError()
    {
        await using var queue = new TaskQueue<string>();
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        await safeQueue.EnqueueAsync("delta", TestContext.Current.CancellationToken);
        var leaseResult = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.True(leaseResult.IsSuccess);
        var lease = leaseResult.Value;

        var result = await lease.FailAsync(null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }

    [Fact]
    public async Task FailAsync_WithRequeue_ShouldReturnSuccess()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<string>(timeProvider: provider);
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        await safeQueue.EnqueueAsync("epsilon", TestContext.Current.CancellationToken);
        var leaseResult = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.True(leaseResult.IsSuccess);
        var lease = leaseResult.Value;
        var error = Error.From("retry", ErrorCodes.TaskQueueAbandoned);

        var fail = await lease.FailAsync(error, requeue: true, TestContext.Current.CancellationToken);
        Assert.True(fail.IsSuccess);

        var nextLeaseResult = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
        Assert.True(nextLeaseResult.IsSuccess);
        var nextLease = nextLeaseResult.Value;
        Assert.Equal(2, nextLease.Attempt);
        Assert.Same(error, nextLease.LastError);
    }

    [Fact]
    public async Task LeaseAsync_WhenCanceled_ShouldReturnCanceledError()
    {
        await using var queue = new TaskQueue<string>();
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await safeQueue.LeaseAsync(cts.Token);
        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }
}
