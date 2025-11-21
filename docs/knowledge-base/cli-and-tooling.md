# CLI & Operator Tooling

Purpose: quick map to scripts and utilities operators rely on.

## Source map
- `tools/profiling-toolkit/collect-baseline.sh` / `.ps1` - one-command capture of counters + traces; sample collection rules in `collection-rules.sample.json`.
- `tools/Hugo.ProfilingAnalyzer` - analyzer scaffolding for profiling outputs.
- `benchmarks/Hugo.Benchmarks` - BenchmarkDotNet harness for repeatable perf measurements.
- `docker/ci/Dockerfile.ci` - reproducible build/test/pack environment used by CI and useful for local isolation.
- `docs/how-to/profiling-toolkit.md` - usage guide for the profiling scripts.

## Responsibilities
- Provide repeatable tooling to inspect hot paths, memory, and queue health without requiring IDEs.
- Keep operational scripts cross-platform (bash and PowerShell) and runnable in minimal containers.

## Backlog references
- File tooling issues when a script diverges from CI expectations or from the perf guidelines; keep collection rules in sync with new metrics.
- Prefer enhancing existing scripts before adding new binaries; if a new tool is required, document invocation and outputs in `docs/how-to/profiling-toolkit.md`.

## Cross-cutting relationships
- Profiling scripts assume `GoDiagnostics` instruments are present; when adding metrics ensure collection rules include them.
- CI workflows (`.github/workflows/docker-ci.yml`, `profiling-baseline.yml`) execute these scripts-changes here can break CI images and should be validated via the docker workflow locally.
- Benchmarks share the same runtime code paths as production; update benchmark cases when adding new primitives to keep perf watch coverage honest.
