# Publish Metrics and Traces to OpenTelemetry

Expose Hugo’s counters, histograms, and activity spans through OpenTelemetry with a single extension method, then layer on exporters and dashboards that fit your environment.

## What you will do

- Install the OpenTelemetry helpers that ship with Hugo.
- Register schema-aware meters and activity sources in a host builder.
- Add custom exporters, samplers, and views when you need fine-grained control.
- Verify that metrics and traces flow to a local collector or dashboard.

Estimated time: 15 minutes.

## Prerequisites

- An application that references Hugo 1.0.0 or later.
- 10 SDK.
- NuGet packages:
  - `Hugo.Diagnostics.OpenTelemetry`
  - `OpenTelemetry.Extensions.Hosting`
  - `OpenTelemetry.Trace`
  - `OpenTelemetry.Instrumentation.Runtime`
  - `OpenTelemetry.Exporter.Otlp` (or your preferred exporter such as `OpenTelemetry.Exporter.Console`).
- Optional (for Aspire deployments): `Aspire.Hosting` 9.0+.

Install the diagnostics helpers:

```bash
dotnet add package Hugo.Diagnostics.OpenTelemetry
```

Add additional instrumentation or exporters with `dotnet add package <PackageId>` as needed.

## Step&nbsp;1 — Enable Hugo diagnostics

```csharp
using Hugo.Diagnostics.OpenTelemetry;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddOpenTelemetry()
    .AddHugoDiagnostics(options =>
    {
        options.ServiceName = builder.Environment.ApplicationName;
        options.OtlpEndpoint = new Uri("http://collector:4317");
        options.AddPrometheusExporter = true;
        options.MaxActivitiesPerInterval = 50;
    });

var app = builder.Build();
app.Run();
```

What this does:

- Registers `GoDiagnostics.MeterName`, `GoDiagnostics.ActivitySourceName`, and the aligned telemetry schema.
- Adds `System.Diagnostics.Metrics` runtime instrumentation so GC/ThreadPool counters emit alongside Hugo metrics.
- Configures OTLP exporters for metrics and traces (gRPC by default, honouring `OtlpEndpoint`).
- Enables an optional Prometheus exporter for `/metrics` scraping.
- Applies rate-limited sampling so high-volume workflows do not overwhelm the tracing backend.

> **Tip:** Call `builder.AddHugoDiagnostics(...)` instead when you only need a single extension point; it wraps `AddOpenTelemetry` for convenience.

## Step&nbsp;2 — Tailor the OpenTelemetry pipeline

Chain additional configuration on the returned `OpenTelemetryBuilder` when you need custom samplers, exporters, or metric views:

```csharp
var otel = builder.Services.AddOpenTelemetry()
    .AddHugoDiagnostics(options =>
    {
        options.ServiceName = "worker";
        options.EnableRateLimitedSampling = false;
    });

otel.WithTracing(tracing =>
{
    tracing.SetSampler(new TraceIdRatioBasedSampler(0.25));
    tracing.AddConsoleExporter();
});

otel.WithMetrics(metrics =>
{
    metrics.AddView(
        instrumentName: "workflow.duration",
        new ExplicitBucketHistogramConfiguration
        {
            Boundaries = new[] { 10d, 50d, 100d, 500d, 1_000d }
        });
});
```

- Disable or reconfigure rate-limited sampling when you rely on downstream sampling instead.
- Add custom exporters (Console, OTLP HTTP, Azure Monitor, etc.).
- Shape histograms to match your SLO buckets.

## Step&nbsp;3 — Integrate with Aspire (optional)

```csharp
using Aspire.Hosting;
using Hugo.Diagnostics.OpenTelemetry;

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

The dashboard discovers Hugo’s meter and activity source as soon as the collector receives data.

> **Note:** Add `using OpenTelemetry.Metrics;` when configuring metric readers so `MetricReaderTemporalityPreference` resolves.

## Step&nbsp;4 — Verify telemetry locally

1. **Run a collector (Prometheus + OTLP):**

    ```bash
    docker run --rm \
        -p 4317:4317 -p 4318:4318 -p 9464:9464 \
        -v "${PWD}/otel-collector.yaml:/etc/otelcol/config.yaml" \
        otel/opentelemetry-collector:0.103.1
    ```

2. **Start your application** (`dotnet run`).

3. **Check Prometheus metrics:**

    ```bash
    curl http://localhost:9464/metrics | rg "hugo"
    ```

4. **Monitor counters on the command line:**

    ```bash
    dotnet-counters monitor \
        --process-id <pid> \
        --refresh-interval 1 \
        --counters "Hugo.Go[waitgroup.additions,channel.select.latency]"
    ```

5. **Inspect traces:**

    - Use the Aspire dashboard, Jaeger, or `dotnet monitor trace --egress console`.
    - Look for activities with the source `Hugo.Go` and tags such as `workflow.namespace`, `workflow.replay_count`, and `channel.name`.

If no data appears, revisit the troubleshooting section below.

## Step&nbsp;5 — Production defaults

- Prefer `OtlpExportProtocol.Grpc` for lower overhead and backpressure support.
- Use batch exporting with a 5‑second interval and batches of up to 512 points to balance freshness and throughput.
- Keep histogram temporality at `Delta` so the collector can aggregate across instances without double counting.
- Persist workflow metadata (`workflow.namespace`, `workflow.task_queue`, `workflow.logical_clock`) as indexed dimensions in your observability backend—Hugo already emits them as metric tags and activity attributes.

```csharp
services.AddOpenTelemetry().AddHugoDiagnostics(options =>
{
    options.OtlpEndpoint = new Uri("http://collector:4317");
    options.OtlpProtocol = OtlpExportProtocol.Grpc;
});

// Example metric reader configuration:
// metrics.ConfigureMetricReader(reader =>
//     reader.TemporalityPreference = MetricReaderTemporalityPreference.Delta);
```

## Troubleshooting

- **Duplicate meters in tests:** Call `GoDiagnostics.Reset()` between test runs to dispose existing meters before registering new ones.
- **Missing metrics or spans:** Confirm the endpoint is reachable, the OTLP exporter subscribes to `GoDiagnostics.MeterName` and `GoDiagnostics.ActivitySourceName`, and the collector exposes the expected ports.
- **Sampling drops spans:** Increase `MaxActivitiesPerInterval`, lengthen `SamplingInterval`, or set `EnableRateLimitedSampling = false` when downstream systems already enforce sampling.
- **Schema mismatches:** Keep `GoDiagnostics.TelemetrySchemaUrl` attached to every resource so backends interpret dimensions consistently.
- **No Prometheus metrics:** Ensure `options.AddPrometheusExporter = true` and that the hosting environment exposes the `/metrics` endpoint (Kestrel or the Aspire dashboard do this automatically).

## Next steps

- Explore the [Diagnostics reference](../reference/diagnostics.md) for a complete list of instruments and tags.
- Wire fan-in pipelines and retries with [Coordinate fan-in workflows](fan-in-channels.md) and [Apply timeout, retry, and cancellation playbooks](playbook-templates.md).
- Study the [`Hugo.WorkerSample`](../../samples/Hugo.WorkerSample/Program.cs) for a production-style worker that publishes metrics and traces out of the box.
