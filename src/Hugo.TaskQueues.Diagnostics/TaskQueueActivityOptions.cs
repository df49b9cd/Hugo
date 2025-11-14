using System.Diagnostics;

namespace Hugo.TaskQueues.Diagnostics;

/// <summary>
/// Configures ActivitySource registration and sampling for TaskQueue operations.
/// </summary>
public sealed class TaskQueueActivityOptions
{
    /// <summary>Gets or sets a value indicating whether the ActivitySource should be configured.</summary>
    public bool ConfigureActivitySource { get; set; } = true;

    /// <summary>Gets or sets the ActivitySource name.</summary>
    public string? Name { get; set; } = GoDiagnostics.ActivitySourceName;

    /// <summary>Gets or sets the ActivitySource semantic version.</summary>
    public string? Version { get; set; } = GoDiagnostics.InstrumentationVersion;

    /// <summary>Gets or sets the optional schema URL applied to the ActivitySource.</summary>
    public Uri? SchemaUrl { get; set; }

    /// <summary>Gets or sets a value indicating whether rate-limited sampling should be enabled.</summary>
    public bool EnableRateLimitedSampling { get; set; } = true;

    /// <summary>Gets or sets the maximum number of sampled activities per interval.</summary>
    public int MaxActivitiesPerInterval { get; set; } = 250;

    /// <summary>Gets or sets the sampling interval applied to the rate limiter.</summary>
    public TimeSpan SamplingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets or sets the sampling decision returned when the rate limit is exceeded.</summary>
    public ActivitySamplingResult UnsampledResult { get; set; } = ActivitySamplingResult.PropagationData;
}
