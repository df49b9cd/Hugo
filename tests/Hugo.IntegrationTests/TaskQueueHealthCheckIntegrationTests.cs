using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;


namespace Hugo.Tests;

[Collection("TaskQueueConcurrency")]
public sealed class TaskQueueHealthCheckIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task AddTaskQueueHealthCheck_ShouldSurfaceQueueBacklogThroughHealthChecks()
    {
        ServiceCollection services = new();
        services.AddSingleton(new TaskQueue<int>(new TaskQueueOptions
        {
            Capacity = 4,
            Name = "integration-queue"
        }));

        services.AddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        services.AddTaskQueueHealthCheck<int>(
            "integration-queue-health",
            sp => sp.GetRequiredService<TaskQueue<int>>(),
            options =>
            {
                options.PendingDegradedThreshold = 1;
                options.PendingUnhealthyThreshold = 3;
                options.ActiveLeaseDegradedThreshold = 1;
                options.ActiveLeaseUnhealthyThreshold = 2;
            });

        await using ServiceProvider provider = services.BuildServiceProvider();

        var queue = provider.GetRequiredService<TaskQueue<int>>();
        var healthChecks = provider.GetRequiredService<HealthCheckService>();

        await queue.EnqueueAsync(1, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(2, TestContext.Current.CancellationToken);
        TaskQueueLease<int> lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);

        HealthReport report = await healthChecks.CheckHealthAsync(TestContext.Current.CancellationToken);

        report.Status.ShouldBe(HealthStatus.Degraded);
        var entry = report.Entries["integration-queue-health"];
        entry.Data.ShouldContainKeyAndValue("taskqueue.pending", 1L);
        entry.Data.ShouldContainKeyAndValue("taskqueue.activeLeases", 1);

        await lease.CompleteAsync(TestContext.Current.CancellationToken);
    }
}
