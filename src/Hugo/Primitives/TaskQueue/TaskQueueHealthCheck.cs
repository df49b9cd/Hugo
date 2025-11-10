using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hugo;

/// <summary>
/// Options that govern the health evaluation of a <see cref="TaskQueue{T}"/>.
/// </summary>
public sealed class TaskQueueHealthCheckOptions
{
    /// <summary>
    /// Gets or sets the pending count that reports a degraded status. Defaults to 1,024.
    /// </summary>
    public long PendingDegradedThreshold { get; set; } = 1_024;

    /// <summary>
    /// Gets or sets the pending count that reports an unhealthy status. Defaults to 2,048.
    /// </summary>
    public long PendingUnhealthyThreshold { get; set; } = 2_048;

    /// <summary>
    /// Gets or sets the active lease count that reports a degraded status. Defaults to 64.
    /// </summary>
    public int ActiveLeaseDegradedThreshold { get; set; } = 64;

    /// <summary>
    /// Gets or sets the active lease count that reports an unhealthy status. Defaults to 128.
    /// </summary>
    public int ActiveLeaseUnhealthyThreshold { get; set; } = 128;
}

/// <summary>
/// Health check implementation that inspects a <see cref="TaskQueue{T}"/>.
/// </summary>
/// <typeparam name="T">Work item type processed by the queue.</typeparam>
public sealed class TaskQueueHealthCheck<T> : IHealthCheck
{
    private readonly TaskQueue<T> _queue;
    private readonly TaskQueueHealthCheckOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskQueueHealthCheck{T}"/> class.
    /// </summary>
    public TaskQueueHealthCheck(TaskQueue<T> queue, TaskQueueHealthCheckOptions options)
    {
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        long pending = _queue.PendingCount;
        int active = _queue.ActiveLeaseCount;

        Dictionary<string, object> data = new(StringComparer.OrdinalIgnoreCase)
        {
            ["taskqueue.pending"] = pending,
            ["taskqueue.activeLeases"] = active
        };

        if (pending >= _options.PendingUnhealthyThreshold || active >= _options.ActiveLeaseUnhealthyThreshold)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("TaskQueue backlog exceeded unhealthy thresholds.", data: data));
        }

        if (pending >= _options.PendingDegradedThreshold || active >= _options.ActiveLeaseDegradedThreshold)
        {
            return Task.FromResult(HealthCheckResult.Degraded("TaskQueue backlog exceeded degraded thresholds.", data: data));
        }

        return Task.FromResult(HealthCheckResult.Healthy(data: data));
    }
}

/// <summary>
/// Extensions that register <see cref="TaskQueueHealthCheck{T}"/> instances.
/// </summary>
public static class TaskQueueHealthCheckBuilderExtensions
{
    /// <summary>
    /// Adds a health check for the specified <see cref="TaskQueue{T}"/>.
    /// </summary>
    public static IServiceCollection AddTaskQueueHealthCheck<T>(
        this IServiceCollection services,
        string name,
        Func<IServiceProvider, TaskQueue<T>> queueFactory,
        Action<TaskQueueHealthCheckOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(queueFactory);

        services.AddHealthChecks().Add(new HealthCheckRegistration(
            name,
            sp =>
            {
                TaskQueueHealthCheckOptions options = new();
                configure?.Invoke(options);
                return new TaskQueueHealthCheck<T>(queueFactory(sp), options);
            },
            failureStatus: null,
            tags: null));

        return services;
    }
}
