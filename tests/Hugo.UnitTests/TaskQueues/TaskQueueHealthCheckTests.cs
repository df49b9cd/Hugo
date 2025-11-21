using Microsoft.Extensions.Diagnostics.HealthChecks;


namespace Hugo.Tests.TaskQueues;

public sealed class TaskQueueHealthCheckTests
{
    [Fact(Timeout = 15_000)]
    public async Task CheckHealthAsync_ShouldReportHealthy_WhenBelowThresholds()
    {
        await using var queue = new TaskQueue<int>(new TaskQueueOptions
        {
            Capacity = 4,
            Name = "unit-queue-healthy"
        });

        TaskQueueHealthCheckOptions options = new()
        {
            PendingDegradedThreshold = 2,
            PendingUnhealthyThreshold = 3,
            ActiveLeaseDegradedThreshold = 2,
            ActiveLeaseUnhealthyThreshold = 3
        };

        var check = new TaskQueueHealthCheck<int>(queue, options);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Healthy);
        result.Data.ShouldContainKeyAndValue("taskqueue.pending", 0L);
        result.Data.ShouldContainKeyAndValue("taskqueue.activeLeases", 0);
    }

    [Fact(Timeout = 15_000)]
    public async Task CheckHealthAsync_ShouldReportDegraded_WhenPendingExceedsThreshold()
    {
        await using var queue = new TaskQueue<int>(new TaskQueueOptions
        {
            Capacity = 4,
            Name = "unit-queue-degraded"
        });

        await queue.EnqueueAsync(42, TestContext.Current.CancellationToken);

        TaskQueueHealthCheckOptions options = new()
        {
            PendingDegradedThreshold = 1,
            PendingUnhealthyThreshold = 4,
            ActiveLeaseDegradedThreshold = 10,
            ActiveLeaseUnhealthyThreshold = 20
        };

        var check = new TaskQueueHealthCheck<int>(queue, options);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Degraded);
        result.Data.ShouldContainKeyAndValue("taskqueue.pending", 1L);
        result.Data.ShouldContainKeyAndValue("taskqueue.activeLeases", 0);
    }

    [Fact(Timeout = 15_000)]
    public async Task CheckHealthAsync_ShouldReportUnhealthy_WhenActiveLeasesExceedThreshold()
    {
        await using var queue = new TaskQueue<int>(new TaskQueueOptions
        {
            Capacity = 4,
            Name = "unit-queue-unhealthy"
        });

        await queue.EnqueueAsync(1, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(2, TestContext.Current.CancellationToken);

        TaskQueueLease<int> firstLease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        TaskQueueLease<int> secondLease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        TaskQueueHealthCheckOptions options = new()
        {
            PendingDegradedThreshold = 10,
            PendingUnhealthyThreshold = 20,
            ActiveLeaseDegradedThreshold = 1,
            ActiveLeaseUnhealthyThreshold = 2
        };

        var check = new TaskQueueHealthCheck<int>(queue, options);

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        result.Status.ShouldBe(HealthStatus.Unhealthy);
        result.Data.ShouldContainKeyAndValue("taskqueue.pending", 0L);
        result.Data.ShouldContainKeyAndValue("taskqueue.activeLeases", 2);

        await firstLease.CompleteAsync(TestContext.Current.CancellationToken);
        await secondLease.CompleteAsync(TestContext.Current.CancellationToken);
    }
}
