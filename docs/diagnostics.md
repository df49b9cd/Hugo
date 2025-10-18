# Diagnostics & Metrics

`Hugo.GoDiagnostics` emits metrics via `System.Diagnostics.Metrics` so applications can light up OpenTelemetry counters without extra plumbing.

## Configuration

```csharp
using Hugo;
using System.Diagnostics.Metrics;

var meterFactory = new MeterFactory();
GoDiagnostics.Configure(meterFactory, meterName: "Hugo.Go");
```

- Call `Configure(IMeterFactory)` during startup to wire counters into your telemetry provider.
- Use `Configure(Meter)` when integrating with a pre-built meter (e.g., from dependency injection).

## Emitted Instruments

| Name | Type | Description |
| ---- | ---- | ----------- |
| `waitgroup.additions` | `Counter<long>` | Number of `WaitGroup.Add` invocations. |
| `waitgroup.completions` | `Counter<long>` | Number of `WaitGroup.Done` calls. |
| `waitgroup.outstanding` | `UpDownCounter<long>` | In-flight operations tracked by wait groups. |
| `result.successes` | `Counter<long>` | Successful `Result<T>` creations. |
| `result.failures` | `Counter<long>` | Failed `Result<T>` creations. |
| `channel.select.attempts` | `Counter<long>` | Attempts to run `Go.SelectAsync`. |
| `channel.select.completions` | `Counter<long>` | Successful select operations. |
| `channel.select.timeouts` | `Counter<long>` | Select operations that timed out. |
| `channel.select.cancellations` | `Counter<long>` | Select operations cancelled. |
| `channel.select.latency` | `Histogram<double>` | Duration of select operations (ms). |
| `channel.depth` | `Histogram<long>` | Observed backlog size when consuming channel values. |

## Testing

Use `GoDiagnostics.Reset()` in unit tests to tear down existing meters and avoid cross-test pollution.
