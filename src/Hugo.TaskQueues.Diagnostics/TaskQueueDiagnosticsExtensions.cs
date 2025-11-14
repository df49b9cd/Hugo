using System.Diagnostics.Metrics;

using Microsoft.Extensions.DependencyInjection;

namespace Hugo.TaskQueues.Diagnostics;

/// <summary>
/// Extension helpers for configuring TaskQueue diagnostics.
/// </summary>
public static class TaskQueueDiagnosticsExtensions
{
    /// <summary>
    /// Adds TaskQueue diagnostics using the supplied <see cref="IMeterFactory"/>.
    /// </summary>
    /// <param name="meterFactory">Factory used to build the meter.</param>
    /// <param name="configure">Optional configuration delegate.</param>
    /// <returns>A registration handle that disposes the instrumentation.</returns>
    public static TaskQueueDiagnosticsRegistration AddTaskQueueDiagnostics(this IMeterFactory meterFactory, Action<TaskQueueDiagnosticsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        var options = new TaskQueueDiagnosticsOptions();
        configure?.Invoke(options);

        return TaskQueueDiagnosticsRegistration.Create(meterFactory, options);
    }

    /// <summary>
    /// Adds TaskQueue diagnostics using the <see cref="IMeterFactory"/> resolved from DI.
    /// </summary>
    /// <param name="serviceProvider">Application service provider.</param>
    /// <param name="configure">Optional configuration delegate.</param>
    /// <returns>A registration handle that disposes the instrumentation.</returns>
    public static TaskQueueDiagnosticsRegistration AddTaskQueueDiagnostics(this IServiceProvider serviceProvider, Action<TaskQueueDiagnosticsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        IMeterFactory meterFactory = serviceProvider.GetRequiredService<IMeterFactory>();
        return meterFactory.AddTaskQueueDiagnostics(configure);
    }
}
