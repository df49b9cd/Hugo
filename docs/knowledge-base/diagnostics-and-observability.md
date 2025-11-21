# Diagnostics & Observability

Purpose: where telemetry lives and how to extend it.

## Source map
- `src/Hugo/GoDiagnostics.cs` - shared meters, histograms, and activity sources used throughout the runtime and queues.
- `src/Hugo.Diagnostics.OpenTelemetry` - DI extensions that bind `GoDiagnostics` to OpenTelemetry exporters (OTLP, Prometheus) and activity propagation.
- `src/Hugo.TaskQueues.Diagnostics` - additional queue metrics/log shipping helpers.
- `docs/reference/diagnostics.md` - canonical list of instruments and activity names.
- `docs/how-to/taskqueue-diagnostics.md` & `docs/how-to/taskqueue-backpressure-monitor.md` - queue-specific dashboards and health wiring.
- `docs/how-to/observe-with-opentelemetry.md` - end-to-end observability wiring.
- `docs/how-to/workflow-visibility.md` - visibility into deterministic workflow state.
- `tools/profiling-toolkit/*` - scripts for collecting counters/traces (`collect-baseline.sh`/`.ps1`, sample collection rules).

## Responsibilities
- Expose low-cardinality, allocation-safe telemetry for channels, selects, task queues, retries, deterministic gates, and workflows.
- Provide plug-and-play OpenTelemetry setup with minimal host code.
- Keep profiling baselines repeatable so perf regressions are caught early.

## Backlog references
- Add new metrics/activity names via `GoDiagnostics` and document them in `docs/reference/diagnostics.md`; coordinate with OTEL package to surface exporters.
- For perf regressions, capture baselines using `tools/profiling-toolkit` and attach artifacts to issues/PRs.
- Watch dimensionality-avoid high-cardinality tags in new instruments; confirm with perf guidelines.

## Cross-cutting relationships
- Task queues and deterministic workflows emit telemetry that operations teams use for SLOs; new runtime features should hook into the same meters.
- Health checks (`TaskQueueHealthCheck<T>`) integrate with ASP.NET Core endpoints; align thresholds with backpressure monitoring to avoid noisy alerts.
- Profiling CI workflow (`.github/workflows/profiling-baseline.yml`) consumes the toolkit scripts; keeping them stable maintains historical comparability.
