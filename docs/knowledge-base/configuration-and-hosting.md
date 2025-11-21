# Configuration & Hosting

Purpose: how Hugo is wired into hosts and configured for environments.

## Source map
- `samples/Hugo.WorkerSample` - canonical generic-host wiring (services, background worker, queue + diagnostics registration).
- `samples/Hugo.DeterministicWorkerSample*` - deterministic worker variants for Cosmos, Redis, relational providers, and a cloud-queue flavor.
- `src/Hugo.Diagnostics.OpenTelemetry` - service collection extensions that register meters/activity sources and OTLP/Prometheus outputs.
- `src/Hugo/TaskQueues` - queue options (capacity, watermarks, lease settings) surfaced via options patterns.
- `docs/how-to/observe-with-opentelemetry.md`, `docs/how-to/taskqueue-diagnostics.md`, `docs/how-to/taskqueue-backpressure-monitor.md` - hosting recipes for observability and queue health.
- `docs/how-to/publish-packages.md` - guidance for distributing internal builds/feeds.
- `docker/ci/Dockerfile.ci` - CI container that restores, builds, tests, and packs using the same host layout as local.

## Responsibilities
- Register Hugo services into ASP.NET Core or worker hosts with minimal ceremony and without breaking trimming/AOT.
- Surface configuration knobs (telemetry endpoints, queue thresholds, deterministic store connection info) through options and environment variables.
- Keep host startup allocation-safe; rely on `TimeProvider` abstractions instead of ambient clocks.

## Backlog references
- When adding host options, update the sample workers and related how-to docs to keep configuration discoverable.
- Track environment-specific issues (OTLP endpoints, TLS, container limits) in GitHub issues and mirror fixes in `docker/ci` where relevant.
- Validate new configuration surfaces against trimming/AOT by running publish in Release and auditing warnings.

## Cross-cutting relationships
- Diagnostics package depends on `GoDiagnostics` meters/activities emitted from the core runtime; host registration must happen before queue/workflow components start.
- Deterministic stores are selected/configured at host level; samples illustrate the expected DI shape for each provider.
- Queue health checks can be plugged into ASP.NET Core health probes; keep queue thresholds aligned with backpressure monitor settings.
