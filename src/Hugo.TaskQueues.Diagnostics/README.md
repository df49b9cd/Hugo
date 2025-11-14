# Hugo.TaskQueues.Diagnostics

`Hugo.TaskQueues.Diagnostics` bundles the TaskQueue-specific instrumentation helpers used by OmniRelay:

- Configures the `GoDiagnostics` meter/activity source with queue-aware tag enrichers.
- Enables rate-limited sampling aligned with .NET 10 defaults.
- Streams `TaskQueueBackpressureMonitor` and `TaskQueueReplicationSource` events into control-plane endpoints through `TaskQueueDiagnosticsHost`.

Refer to `docs/reference/diagnostics.md` for the complete list of emitted instruments and `docs/how-to/taskqueue-diagnostics.md` for a walkthrough that wires the package into OmniRelay.
