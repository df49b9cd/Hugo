# Publish Metrics and Traces to OpenTelemetry

Use this guide to expose `GoDiagnostics` metrics and tracing spans through OpenTelemetry. The steps below adopt the library's default telemetry schema URL, apply rate-limited sampling, and show how to feed the Aspire dashboard while shipping data to an OTLP collector.

## Prerequisites

- An application using Hugo 1.0.0 or later.
- NuGet packages:
  - `OpenTelemetry.Extensions.Hosting`
  - `OpenTelemetry.Instrumentation.Runtime`
  - `OpenTelemetry.Trace`
  - An exporter package such as `OpenTelemetry.Exporter.Otlp` (recommended) or `OpenTelemetry.Exporter.Console`.
- Optional for Aspire samples: `Aspire.Hosting` 9.0 or later.

## Configure OpenTelemetry with the Hugo schema

```csharp
using Hugo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddOpenTelemetry()
   .ConfigureResource(resource => resource
      .AddService("worker")
      .AddTelemetrySdk()
      .AddAttributes(new[]
      {
         new KeyValuePair<string, object?>("telemetry.schema.url", GoDiagnostics.TelemetrySchemaUrl)
      }))
   .WithMetrics(metrics =>
   {
      metrics.AddRuntimeInstrumentation();
      metrics.AddMeter(GoDiagnostics.MeterName);
      metrics.AddOtlpExporter();
   })
   .WithTracing(tracing =>
   {
      tracing.AddSource(GoDiagnostics.ActivitySourceName);
      tracing.SetSampler(new TraceIdRatioBasedSampler(1.0));
      tracing.AddOtlpExporter();
   });

var services = builder.Services.BuildServiceProvider();

var meterFactory = services.GetRequiredService<IMeterFactory>();
GoDiagnostics.Configure(meterFactory);

var activitySource = GoDiagnostics.CreateActivitySource();
GoDiagnostics.Configure(activitySource);

using var limiter = GoDiagnostics.UseRateLimitedSampling(
   activitySource,
   maxActivitiesPerInterval: 50,
   interval: TimeSpan.FromSeconds(1));

var app = builder.Build();
app.Run();
```

- `GoDiagnostics.TelemetrySchemaUrl` keeps metrics and spans aligned with the latest OpenTelemetry semantic conventions.
- `GoDiagnostics.CreateActivitySource` produces an `ActivitySource` using the same schema URL and library version as the default meter.
- `UseRateLimitedSampling` throttles high-volume spans while still propagating context. Dispose the returned object on shutdown to remove the listener.

## Aspire dashboard integration

When you host the application inside an Aspire distributed app, register the instrumentation before creating your app host and wire the dashboard exporters:

```csharp
var appHost = DistributedApplication.CreateBuilder(args);

appHost.Services.AddOpenTelemetry()
   .WithMetrics(metrics => metrics
   // Configure your metric reader to prefer delta temporality so aggregations remain additive.
      .AddOtlpExporter())
   .WithTracing(tracing => tracing

Most OpenTelemetry builders expose a `ConfigureMetricReader` or equivalent hook where you can set `reader.TemporalityPreference = MetricReaderTemporalityPreference.Delta` to meet the recommendation above.
      .AddSource(GoDiagnostics.ActivitySourceName)
      .SetSampler(new TraceIdRatioBasedSampler(1.0))
      .AddOtlpExporter());

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
services.AddOpenTelemetry().WithMetrics(metrics =>
{
   metrics.AddMeter(GoDiagnostics.MeterName);
   metrics.AddOtlpExporter(options =>
   {
      options.Endpoint = new Uri("http://collector:4317");
      options.Protocol = OtlpExportProtocol.Grpc;
      options.ExportProcessorType = ExportProcessorType.Batch;
      options.BatchExportProcessorOptions = new BatchExportProcessorOptions<Activity>
      {
         MaxExportBatchSize = 512,
         ScheduledDelayMilliseconds = 5000
      };
   });
   metrics.SetTemporalityPreference(MetricReaderTemporalityPreference.Delta);
});
```

## Troubleshooting

- **Duplicate meters**: Call `GoDiagnostics.Reset()` in test fixtures to avoid re-registering meters between runs.
- **Missing metrics or spans**: Confirm the OTLP exporter subscribes to `GoDiagnostics.MeterName` and `GoDiagnostics.ActivitySourceName` and that the collector accepts traffic from the configured endpoint.
- **Aggressive sampling**: If `UseRateLimitedSampling` drops too many spans, increase `maxActivitiesPerInterval` or disable the helper for low-volume workloads.
- **Schema mismatches**: Verify every resource attaches `GoDiagnostics.TelemetrySchemaUrl` so downstream systems interpret dimensions consistently.

## Related reference

- [Diagnostics surface](../reference/diagnostics.md)
- [Concurrency primitives](../reference/concurrency-primitives.md)
