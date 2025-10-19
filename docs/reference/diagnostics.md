# Diagnostics Reference

`GoDiagnostics` emits `System.Diagnostics.Metrics` instruments that you can export through OpenTelemetry or other meter providers. Configure diagnostics via `GoDiagnostics.Configure(IMeterFactory, string meterName = "Hugo.Go")`.

## Instruments

| Name | Type | Unit | Description |
| ---- | ---- | ---- | ----------- |
| `waitgroup.additions` | `Counter<long>` | operations | Incremented when `WaitGroup.Add` or `WaitGroup.Go` schedules work. |
| `waitgroup.completions` | `Counter<long>` | operations | Incremented when tracked work completes. |
| `waitgroup.outstanding` | `UpDownCounter<long>` | operations | Tracks in-flight wait-group operations. |
| `result.successes` | `Counter<long>` | pipelines | Incremented when `Result<T>` instances succeed. |
| `result.failures` | `Counter<long>` | pipelines | Incremented on failures, including errors surfaced via `TapError`. |
| `channel.select.attempts` | `Counter<long>` | operations | Incremented before calling `Go.SelectAsync`. |
| `channel.select.completions` | `Counter<long>` | operations | Incremented when a select case completes successfully. |
| `channel.select.timeouts` | `Counter<long>` | operations | Incremented when select operations hit the configured timeout. |
| `channel.select.cancellations` | `Counter<long>` | operations | Incremented when select operations observe cancellation tokens. |
| `channel.select.latency` | `Histogram<double>` | milliseconds | Measures elapsed time spent inside `Go.SelectAsync`. |
| `channel.depth` | `Histogram<long>` | messages | Samples the backlog size when reading from channels. |

## Configuration options

- Call `GoDiagnostics.Configure(IMeterFactory factory, string meterName)` during startup to bind instruments to an existing `MeterProvider`.
- Use `GoDiagnostics.Configure(Meter meter)` when DI already exposes a pre-built `Meter`.
- Invoke `GoDiagnostics.Reset()` (typically in unit tests) to dispose existing meters before registering new ones.

## Usage guidelines

- Register the meter before creating wait groups or channels to ensure counters record the full lifecycle.
- Add exporters (Console, OTLP, Prometheus) through your telemetry provider to ship metrics to backends.
- Combine with [Publish metrics to OpenTelemetry](../how-to/observe-with-opentelemetry.md) for a full setup walkthrough.
