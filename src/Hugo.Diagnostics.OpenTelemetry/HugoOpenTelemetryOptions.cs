using System;
using System.Diagnostics;
using Hugo;
using OpenTelemetry.Exporter;

namespace Hugo.Diagnostics.OpenTelemetry;

/// <summary>
/// Configures OpenTelemetry integration for <see cref="GoDiagnostics"/>.
/// </summary>
public sealed class HugoOpenTelemetryOptions
{
    /// <summary>
    /// Gets or sets the semantic conventions schema URL attached to the resource and instruments.
    /// </summary>
    public string SchemaUrl { get; set; } = GoDiagnostics.TelemetrySchemaUrl;

    /// <summary>
    /// Gets or sets the service name recorded in the OpenTelemetry resource.
    /// </summary>
    public string? ServiceName { get; set; }

    /// <summary>
    /// Gets or sets the meter name that Hugo diagnostics emit.
    /// </summary>
    public string MeterName { get; set; } = GoDiagnostics.MeterName;

    /// <summary>
    /// Gets or sets the activity source name used for workflow spans.
    /// </summary>
    public string ActivitySourceName { get; set; } = GoDiagnostics.ActivitySourceName;

    /// <summary>
    /// Gets or sets the activity source version advertised to telemetry backends.
    /// </summary>
    public string ActivitySourceVersion { get; set; } = GoDiagnostics.InstrumentationVersion;

    /// <summary>
    /// Gets or sets a value indicating whether the schema attribute should be attached to the OpenTelemetry resource.
    /// </summary>
    public bool AttachSchemaAttribute { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether <see cref="GoDiagnostics.Configure(System.Diagnostics.Metrics.IMeterFactory, string?)"/> should be invoked automatically.
    /// </summary>
    public bool ConfigureMeter { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether an <see cref="ActivitySource"/> should be created for <see cref="GoDiagnostics"/>.
    /// </summary>
    public bool ConfigureActivitySource { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether rate limited sampling should be enabled for workflow spans.
    /// </summary>
    public bool EnableRateLimitedSampling { get; set; } = true;

    /// <summary>
    /// Gets or sets the maximum number of activities that may be sampled within the configured interval.
    /// </summary>
    public int MaxActivitiesPerInterval { get; set; } = 100;

    /// <summary>
    /// Gets or sets the sampling interval used for rate limiting.
    /// </summary>
    public TimeSpan SamplingInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets the sampling result returned when the rate limit is exceeded.
    /// </summary>
    public ActivitySamplingResult UnsampledActivityResult { get; set; } = ActivitySamplingResult.PropagationData;

    /// <summary>
    /// Gets or sets a value indicating whether the OTLP exporter should be registered for metrics and traces.
    /// </summary>
    public bool AddOtlpExporter { get; set; } = true;

    /// <summary>
    /// Gets or sets the OTLP export protocol.
    /// </summary>
    public OtlpExportProtocol OtlpProtocol { get; set; } = OtlpExportProtocol.Grpc;

    /// <summary>
    /// Gets or sets the OTLP endpoint. When null, the default exporter endpoint is used.
    /// </summary>
    public Uri? OtlpEndpoint { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the Prometheus exporter should be registered for metrics.
    /// </summary>
    public bool AddPrometheusExporter { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether runtime instrumentation should be registered.
    /// </summary>
    public bool AddRuntimeInstrumentation { get; set; } = true;
}
