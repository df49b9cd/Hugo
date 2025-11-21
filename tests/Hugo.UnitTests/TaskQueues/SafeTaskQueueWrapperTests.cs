using Hugo.TaskQueues;


namespace Hugo.UnitTests.TaskQueues;

public sealed class SafeTaskQueueWrapperTests
{
    [Fact(Timeout = 15_000)]
    public async Task DisposeAsync_WithOwnership_ShouldDisposeUnderlyingQueue()
    {
        var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "owned", Capacity = 4 });
        await using var wrapper = new SafeTaskQueueWrapper<int>(queue, ownsQueue: true);

        await wrapper.DisposeAsync();

        await Should.ThrowAsync<ObjectDisposedException>(async () =>
        {
            await queue.EnqueueAsync(1, TestContext.Current.CancellationToken);
        });
    }

    [Fact(Timeout = 15_000)]
    public async Task DisposeAsync_WithoutOwnership_ShouldLeaveQueueUsable()
    {
        var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "shared", Capacity = 4 });
        await using var wrapper = new SafeTaskQueueWrapper<int>(queue, ownsQueue: false);

        await wrapper.DisposeAsync();

        await queue.EnqueueAsync(42, TestContext.Current.CancellationToken);
        TaskQueueLease<int> lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        lease.Value.ShouldBe(42);
        await lease.CompleteAsync(TestContext.Current.CancellationToken);
    }

    [Fact(Timeout = 15_000)]
    public async Task EnqueueAsync_ShouldReturnFailure_WhenQueueDisposed()
    {
        var queue = new TaskQueue<int>(new TaskQueueOptions { Name = "disposed", Capacity = 2 });
        await queue.DisposeAsync();
        await using var wrapper = new SafeTaskQueueWrapper<int>(queue, ownsQueue: false);

        Result<Go.Unit> result = await wrapper.EnqueueAsync(7, TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error!.Code.ShouldBe(ErrorCodes.TaskQueueDisposed);
    }
}
