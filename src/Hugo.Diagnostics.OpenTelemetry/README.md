# Hugo.Diagnostics.OpenTelemetry

Hugo ships a batteries-included OpenTelemetry integration so your apps can publish metrics and traces for channels, wait groups, task queues, deterministic workflows, and result pipelines with one registration call. This package wires `GoDiagnostics` into .NET telemetry builders, exports schema-aware instruments, and enables rate-limited sampling that keeps high-volume workloads observable without drowning tracing backends.

## Features

- One-line registration for Hugo meters and activity sources via `AddHugoDiagnostics`.
- Opinionated defaults for OTLP (gRPC/HTTP) and Prometheus exporters that match Aspire ServiceDefaults.
- Optional console exporters and runtime instrumentation for rapid local debugging.
- Rate-limited activity sampling so channel-heavy workloads remain traceable.
- Hooks for customizing samplers, metric views, and exporter pipelines.

## Install

```bash
dotnet add package Hugo.Diagnostics.OpenTelemetry
```

## Quick start

```csharp
using Hugo.Diagnostics.OpenTelemetry;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Trace;

var builder = Host.CreateApplicationBuilder(args);

var otel = builder.Services.AddOpenTelemetry()
    .AddHugoDiagnostics(options =>
    {
        options.ServiceName = builder.Environment.ApplicationName;
        options.OtlpEndpoint = new Uri("http://localhost:4317");
        options.AddPrometheusExporter = true;
        options.MaxActivitiesPerInterval = 64;
    });

otel.WithTracing(tracing =>
{
    tracing.SetSampler(new TraceIdRatioBasedSampler(0.25));
    tracing.AddConsoleExporter();
});

otel.WithMetrics(metrics =>
{
    metrics.AddConsoleExporter();
});

var app = builder.Build();
await app.RunAsync();
```

## Configuration highlights

- `ServiceName`, `SchemaUrl`, and meter/activity names default to Hugo’s telemetry conventions.
- Toggle OTLP exporters (`options.OtlpEndpoint`, `options.OtlpProtocol`) or Prometheus scraping (`options.AddPrometheusExporter`).
- Enable or disable rate-limited sampling (`options.EnableRateLimitedSampling`, `options.MaxActivitiesPerInterval`, `options.SamplingInterval`).
- Add runtime instrumentation (`options.AddRuntimeInstrumentation`) for GC/ThreadPool counters alongside Hugo metrics.

## Documentation

- [Publish metrics and traces to OpenTelemetry](https://github.com/df49b9cd/Hugo/blob/main/docs/how-to/observe-with-opentelemetry.md)
- [Diagnostics reference](https://github.com/df49b9cd/Hugo/blob/main/docs/reference/diagnostics.md)
- [Concurrency primitives reference](https://github.com/df49b9cd/Hugo/blob/main/docs/reference/concurrency-primitives.md)

Need help or have feedback? Open an issue at [github.com/df49b9cd/Hugo](https://github.com/df49b9cd/Hugo/issues).
