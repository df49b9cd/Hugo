# Capture Baselines with the Profiling Toolkit

Collect reproducible counters, traces, and diagnostic bundles from Hugo workloads using the ready-made scripts under `tools/profiling-toolkit`. The recipes work on macOS, Linux, and Windows and mirror the automation that runs in CI.

## When to use this guide

- You need a repeatable performance baseline before shipping a change.
- You want objective data (counters, CPU traces, runtime metrics) to investigate regressions.
- You plan to compare local measurements with GitHub Actions artefacts.

## Toolkit contents

- `collect-baseline.sh` / `collect-baseline.ps1` — orchestrate `dotnet-counters`, `dotnet-trace`, and Speedscope export.
- `collection-rules.sample.json` — drop into `dotnet monitor` for automated capture on thresholds.
- `Hugo.ProfilingAnalyzer` — CLI that summarises captured counters and traces.

All scripts write to `artifacts/profiling/<timestamp>` by default so you can diff runs over time.

## Prerequisites

- Hugo 1.0.0 or later.
- .NET 10 SDK (required for one-shot `dnx` tooling).
- Access to the target process (local PID, container diagnostics port, or remote machine).
- Install or prepare on-demand diagnostics tools:

    ```bash
    dotnet tool install --global dotnet-counters
    dotnet tool install --global dotnet-trace
    dotnet tool install --global dotnet-monitor
    ```

    > **Tip:** On .NET 10 SDK builds you can skip installation and run `dnx dotnet-counters`, `dnx dotnet-trace`, or `dnx dotnet-monitor` directly.

## Step&nbsp;1 — Locate the process

```bash
dotnet-trace ps
```

- Copy the target process ID (PID).
- For containers, expose a diagnostic socket (`DOTNET_DiagnosticPorts`) and forward it to the host (see the [.NET diagnostics guide](https://learn.microsoft.com/dotnet/core/diagnostics/diagnostic-cli-tools#configure-network-ports)).

## Step&nbsp;2 — Capture counters

Monitor live counters:

```bash
dotnet-counters monitor \
    --process-id <PID> \
    --refresh-interval 1 \
    --counters "System.Runtime[gc-heap-size,threadpool-completed-items],Hugo.Go[waitgroup.additions,channel.select.latency]"
```

Capture a one-minute snapshot to CSV:

```bash
dotnet-counters collect \
    --process-id <PID> \
    --output counters.csv \
    --duration 00:01:00
```

Key Hugo counters:

- `Hugo.Go.waitgroup.additions` / `Hugo.Go.waitgroup.outstanding` — coordination pressure.
- `Hugo.Go.channel.select.latency` — stalled select loops.
- `Hugo.Go.taskqueue.*` — lease churn, heartbeats, and dead-letter routing.

## Step&nbsp;3 — Record an EventPipe trace

```bash
dotnet-trace collect \
    --process-id <PID> \
    --duration 00:00:30 \
    --providers "Microsoft-DotNETCore-SampleProfiler,System.Runtime:4:1"
```

- `Microsoft-DotNETCore-SampleProfiler` samples CPU stacks.
- `System.Runtime:4:1` surfaces GC, thread pool, and lock contention events.
- Add `System.Diagnostics.Metrics:0:Informational:FilterSpecs=Meter=Hugo.Go` to capture Hugo meters alongside runtime events.

Convert to Speedscope:

```bash
dotnet-trace convert \
    --format speedscope \
    --output hugo.speedscope.json \
    hugo.nettrace
```

## Step&nbsp;4 — Automate with `dotnet-monitor`

Use `dotnet monitor` when you need scripted or remote collection:

```bash
dotnet monitor collect \
    --process-id <PID> \
    --metrics \
    --logs "LogLevel=Warning" \
    --trace "Duration=00:00:45" \
    --output ./artifacts/profiling
```

Automate thresholds with the sample rules:

```bash
dotnet monitor collect \
    --no-http-egress \
    --configurationFile ./tools/profiling-toolkit/collection-rules.sample.json
```

- Adjust the process filter and thresholds before deploying.
- Configure egress (filesystem, Azure Blob, OTLP) to automatically archive diagnostics.

## Step&nbsp;5 — Use the helper scripts

- **macOS/Linux:** `./tools/profiling-toolkit/collect-baseline.sh --pid <PID>`
- **Windows:** `pwsh ./tools/profiling-toolkit/collect-baseline.ps1 -Name <process>`

Scripts perform:

1. Counter collection into `counters.csv`.
2. EventPipe trace capture.
3. Speedscope conversion.
4. Consistent folder naming under `artifacts/profiling/<timestamp>`.

Override defaults with `--duration`, `--trace-duration`, or `--artifact-root`.

## Step&nbsp;6 — Analyse results

Run the analyzer on the captured folder:

```bash
dotnet run \
    --project tools/Hugo.ProfilingAnalyzer/Hugo.ProfilingAnalyzer.csproj \
    -- artifacts/profiling/<timestamp>
```

Useful flags:

- `--provider Hugo.Go` — focus on Hugo-specific counters.
- `--findings-only` — print high-impact observations (wait-group leaks, select latency spikes).
- `--speedscope artifacts/profiling/<timestamp>/trace.speedscope.json` — summarise hot frames.
- `--sort p95` — highlight the slowest counters first.

## Step&nbsp;7 — Compare baselines

1. Commit or archive a known-good baseline.
2. Re-run the toolkit after changes.
3. Use `git diff --stat artifacts/profiling` or Speedscope comparisons to identify regressions.
4. Share the `.nettrace` and Speedscope JSON with the team when collaborating on investigations.

## Automate in GitHub Actions

The `.github/workflows/profiling-baseline.yml` workflow mirrors the local scripts:

1. Trigger **Actions ▸ Hugo Profiling Baseline ▸ Run workflow**.
2. Provide overrides (project path, target framework, durations) if necessary.
3. Download `profiling-<run-id>` and `worker-log-<run-id>` artefacts for analysis.

Workflow inputs map directly to script parameters (sample project path, runtime version, warm-up duration).

## Troubleshooting

- **Empty Hugo counters:** Ensure `GoDiagnostics.Configure` or `AddHugoDiagnostics` runs before creating channels or wait groups; counters only emit after instrumentation is wired.
- **Permission denied:** Run diagnostics with elevated privileges or grant container capability `--cap-add=SYS_PTRACE`.
- **High overhead:** Reduce durations, narrow providers (`System.Runtime:4:0x8` for GC only), or lower counter refresh intervals.
- **`dnx` not found:** Install the .NET 10 SDK preview or revert to globally installed dotnet tools.
- **dotnet-monitor cannot connect:** Verify diagnostic port paths and disable HTTPS dev certificates with `--tls false` during local testing.

## Related guides

- [Publish metrics and traces to OpenTelemetry](observe-with-opentelemetry.md)
- [Diagnostics reference](../reference/diagnostics.md) for instrument names and units
- [.NET diagnostics overview](https://learn.microsoft.com/dotnet/core/diagnostics/)
