# Samples & Workflows

Purpose: where to see Hugo in action and how to navigate the walkthroughs.

## Source map
- `samples/Hugo.WorkerSample` - baseline worker showing channels, task queues, retries, and telemetry wiring.
- `samples/Hugo.TaskQueueDiagnosticsHostSample` - host focused on queue diagnostics surfaces.
- `samples/Hugo.DeterministicWorkerSample*` - deterministic worker variants split by provider (in-memory core, relational, cloud queue, Redis, Cosmos).
- `docs/tutorials/*.md` - step-by-step walkthroughs for sockets, fan-in/out patterns, deterministic upgrades, and streaming scenarios.
- `docs/how-to/playbook-templates.md`, `docs/how-to/fan-in-channels.md`, `docs/how-to/workflow-visibility.md` - reusable recipes for common orchestration patterns.
- `docs/deterministic-persistence-providers.md` - configuration notes for deterministic sample variants.

## Responsibilities
- Demonstrate expected wiring (DI, `TimeProvider`, telemetry) that mirrors production guidance.
- Provide runnable repros for bugs/regressions; align tests with sample behaviors to keep coverage representative.

## Backlog references
- When adding features, extend the closest sample to show real-world wiring and update the matching tutorial/how-to.
- Keep deterministic provider samples in sync with provider packages; file issues if a sample diverges from the API surface.
- Samples double as perf/profiling targets; refresh baselines when altering hot paths.

## Cross-cutting relationships
- Tutorials map onto code in `samples/`; cross-linking keeps docs truthful-update both when changing flows.
- Deterministic samples depend on `docs/deterministic-persistence-providers.md` guidance; changes to providers should revise that doc and the sample configs.
- Queue/telemetry samples show how `GoDiagnostics` surfaces are consumed; reuse that wiring in tests and host templates to avoid configuration drift.
