# Publish Metrics to OpenTelemetry

Use this guide to expose `GoDiagnostics` counters and histograms through OpenTelemetry so you can inspect wait-group usage, select latency, and channel backlog.

## Prerequisites

- An application using Hugo 1.0.0 or later.
- `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Instrumentation.Runtime` NuGet packages.

## Steps

1. **Install packages**

   ```bash
   dotnet add package OpenTelemetry.Extensions.Hosting
   dotnet add package OpenTelemetry.Exporter.Console
   ```

   Adjust the exporter to match your target (Console, OTLP, Prometheus, etc.).

2. **Register OpenTelemetry**

   ```csharp
   using Hugo;
   using Microsoft.Extensions.DependencyInjection;
   using OpenTelemetry.Metrics;

   var builder = Host.CreateApplicationBuilder(args);

   builder.Services.AddOpenTelemetry()
       .WithMetrics(metrics =>
       {
           metrics.AddRuntimeInstrumentation();
           metrics.AddMeter("Hugo.Go");
           metrics.AddConsoleExporter();
       });
   ```

3. **Configure `GoDiagnostics`**

   ```csharp
   using System.Diagnostics.Metrics;

   IMeterFactory meterFactory = builder.Services.BuildServiceProvider()
       .GetRequiredService<IMeterFactory>();

   GoDiagnostics.Configure(meterFactory, meterName: "Hugo.Go");
   ```

   Call `Configure` once during startup before you create channels or results.

4. **Emit metrics**

   Use Hugo as usual. For example:

   ```csharp
   var wg = new WaitGroup();
   wg.Go(async () => await Task.Delay(100));
   await wg.WaitAsync(CancellationToken.None);
   ```

   The wait-group counters increment automatically.

5. **Verify output**

   Run the application; the console exporter prints metrics similar to:

   ```text
   Counter: waitgroup.additions 1
   Histogram: channel.select.latency count=3 sum=4.1e-3
   ```

   If you are using OTLP exporters, inspect your collector or backend (for example, the OpenTelemetry Collector or Azure Monitor) to confirm the data arrives.

## Troubleshooting

- **Duplicate meters**: Call `GoDiagnostics.Reset()` in test fixtures to avoid re-registering meters between test runs.
- **Missing metrics**: Ensure your exporter subscribes to the `Hugo.Go` meter and your application remains alive long enough for the exporter to flush.
- **Performance impact**: Configure histogram boundaries via `MeterProviderBuilder` when high-cardinality data causes storage pressure.

## Related reference

- [Diagnostics surface](../reference/diagnostics.md)
- [Concurrency primitives](../reference/concurrency-primitives.md)
