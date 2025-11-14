namespace Hugo.TaskQueues.Diagnostics;

/// <summary>
/// Aggregates the configuration necessary to bootstrap TaskQueue diagnostics.
/// </summary>
public sealed class TaskQueueDiagnosticsOptions
{
    /// <summary>Gets or sets a value indicating whether <see cref="GoDiagnostics"/> should configure its meter automatically.</summary>
    public bool ConfigureMeter { get; set; } = true;

    /// <summary>Gets or sets the meter name override applied when configuring <see cref="GoDiagnostics"/>.</summary>
    public string? MeterName { get; set; }

    /// <summary>Gets or sets a value indicating whether <see cref="GoDiagnostics.Reset"/> should run when the registration is disposed.</summary>
    public bool ResetOnDispose { get; set; } = true;

    /// <summary>Gets the metric configuration.</summary>
    public TaskQueueMetricsOptions Metrics { get; } = new();

    /// <summary>Gets the activity configuration.</summary>
    public TaskQueueActivityOptions Activity { get; } = new();
}
