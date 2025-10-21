# Publish Metrics and Traces to OpenTelemetry

Use this guide to expose `GoDiagnostics` metrics and tracing spans through OpenTelemetry. Start with the turnkey helpers from `Hugo.Diagnostics.OpenTelemetry`, then layer on any advanced configuration you need.

## Prerequisites

- An application using Hugo 1.0.0 or later.
- NuGet packages:
  - `Hugo.Diagnostics.OpenTelemetry`
  - `OpenTelemetry.Extensions.Hosting`
  - `OpenTelemetry.Instrumentation.Runtime`
  - `OpenTelemetry.Trace`
  - `OpenTelemetry.Exporter.Otlp` (recommended) or another metrics exporter such as `OpenTelemetry.Exporter.Console`.
- Optional for Aspire samples: `Aspire.Hosting` 9.0 or later.

## Enable Hugo diagnostics with one call

```csharp
using Hugo.Diagnostics.OpenTelemetry;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.AddHugoDiagnostics(options =>
{
    options.ServiceName = "worker";
    options.OtlpEndpoint = new Uri("http://collector:4317");
    options.AddPrometheusExporter = true;
    options.MaxActivitiesPerInterval = 50;
});

var app = builder.Build();
app.Run();
```

The extension configures:

- Schema-aware meters and activity sources using `GoDiagnostics.TelemetrySchemaUrl`.
- OTLP exporters for metrics and traces (gRPC by default, honoring `OtlpEndpoint`).
- Prometheus scraping (disable with `options.AddPrometheusExporter = false`).
- Runtime instrumentation and rate-limited sampling so high-volume spans do not overwhelm your backend.

## Customize the OpenTelemetry pipeline

When you need additional exporters or sampler policies, chain further configuration on the returned `OpenTelemetryBuilder`:

```csharp
var otel = builder.Services.AddOpenTelemetry()
    .AddHugoDiagnostics(options =>
    {
        options.ServiceName = builder.Environment.ApplicationName;
        options.EnableRateLimitedSampling = false;
    });

otel.WithTracing(tracing =>
{
    tracing.SetSampler(new TraceIdRatioBasedSampler(0.25));
    tracing.AddConsoleExporter();
});

otel.WithMetrics(metrics =>
{
    metrics.AddView(instrumentName: "workflow.duration", histogramConfiguration: new ExplicitBucketHistogramConfiguration
    {
        Boundaries = new double[] { 10, 50, 100, 500, 1000 }
    });
});
```

Manual control stays available—the helper simply wires the Hugo-specific plumbing for you.

## Aspire dashboard integration

When running inside an Aspire distributed app, call the same extension before building your host:

```csharp
var appHost = DistributedApplication.CreateBuilder(args);

appHost.Services.AddOpenTelemetry()
    .AddHugoDiagnostics(options =>
    {
        options.ServiceName = "worker";
    });

appHost.AddOpenTelemetryCollector("otel")
    .WithEndpoint("http://localhost:4317")
    .WithMetrics(m => m.WithTemporalityPreference(MetricReaderTemporalityPreference.Delta));

appHost.AddAspireDashboard("dashboard")
    .WithSubscription("otel");

var app = appHost.Build();
app.Run();
```

The dashboard automatically discovers the Hugo meter and activity source once the collector receives data.

## Storage and OTLP exporter defaults

Adopt the following defaults when exporting enriched workflow metrics to an OTLP backend:

- Prefer gRPC (`OtlpExportProtocol.Grpc`) for lower overhead and backpressure support.
- Use batch processing with a 5-second export interval and batches of up to 512 points to balance freshness and throughput.
- Keep histogram temporality to `Delta` so the collector can aggregate across instances without double-counting.
- Persist workflow metadata (`workflow.namespace`, `workflow.task_queue`, etc.) as indexed dimensions in your backend; they originate from Hugo's structured tags.

```csharp
services.AddOpenTelemetry().AddHugoDiagnostics(options =>
{
    options.OtlpEndpoint = new Uri("http://collector:4317");
    options.OtlpProtocol = OtlpExportProtocol.Grpc;
});

// Many OpenTelemetry builders expose ConfigureMetricReader so you can set:
// reader.TemporalityPreference = MetricReaderTemporalityPreference.Delta;
```

## Troubleshooting

- **Duplicate meters**: Call `GoDiagnostics.Reset()` in test fixtures to avoid re-registering meters between runs.
- **Missing metrics or spans**: Confirm the OTLP exporter subscribes to `GoDiagnostics.MeterName` and `GoDiagnostics.ActivitySourceName` and that the collector accepts traffic from the configured endpoint.
- **Aggressive sampling**: If rate limiting drops too many spans, increase `MaxActivitiesPerInterval` or disable it with `EnableRateLimitedSampling = false`.
- **Schema mismatches**: Ensure every resource attaches `GoDiagnostics.TelemetrySchemaUrl` so downstream systems interpret dimensions consistently.

## Related reference

- [Diagnostics surface](../reference/diagnostics.md)
- [Concurrency primitives](../reference/concurrency-primitives.md)
- Sample: [Hugo.WorkerSample](../../samples/Hugo.WorkerSample/Program.cs) shows `AddHugoDiagnostics` in a hosted worker.
