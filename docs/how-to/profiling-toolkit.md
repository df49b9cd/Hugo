# Capture Baselines with the Profiling Toolkit

Use this guide to capture reproducible traces and runtime counters from Hugo workloads with the .NET diagnostic toolchain. Combine `dotnet-counters`, `dotnet-trace`, and `dotnet-monitor` to detect throughput and GC regressions before they reach production. Check in the ready-made helpers under `tools/profiling-toolkit` if you need scripts or collection rule templates.

## Quickstart

- macOS/Linux: run `./tools/profiling-toolkit/collect-baseline.sh --pid <PID>` to capture counters, a CPU trace, and a SpeedScope export into `artifacts/profiling/<timestamp>`.
- Windows: run `pwsh ./tools/profiling-toolkit/collect-baseline.ps1 -Name <process>` for the PowerShell equivalent.
- Automated collection: copy `tools/profiling-toolkit/collection-rules.sample.json` into your `dotnet monitor` deployment and adjust the process filter, thresholds, and egress paths.

Each script falls back to the [`dnx` on-demand launcher](https://learn.microsoft.com/dotnet/core/tools/dotnet-tool-exec) for .NET 10 SDKs, so you can run the diagnostics without permanently installing the global tools.

## Prerequisites

- An application built on Hugo 1.0.0 or later and the .NET 10 SDK.
- Access to the process you want to inspect (local PID, container, or remote diagnostic port).
- The .NET diagnostic global tools:

  ```bash
  dotnet tool install --global dotnet-counters
  dotnet tool install --global dotnet-trace
  dotnet tool install --global dotnet-monitor
  ```

    If the tools are already installed, run `dotnet tool update --global <tool>` to pick up the latest release before profiling. On .NET 10 or later you can also run them one-shot with `dnx dotnet-counters --version`, `dnx dotnet-trace --version`, and `dnx dotnet-monitor --version` without installing anything globally.

## Step 1: Locate the process

List eligible processes and copy the target process identifier (PID):

```bash
dotnet-trace ps
```

For containerized workloads, expose a diagnostic socket by adding `DOTNET_DiagnosticPorts` to the container environment and forwarding it to the host. The [.NET diagnostics guide](https://learn.microsoft.com/dotnet/core/diagnostics/diagnostic-cli-tools#configure-network-ports) covers the full configuration.

> Tip: If the global tools are not installed yet, `dnx dotnet-trace ps` provides the same listing on .NET 10 SDK builds.

## Step 2: Monitor hot counters with `dotnet-counters`

Attach to the process for a live view of Hugo and runtime counters:

```bash
dotnet-counters monitor \
    --process-id <PID> \
    --refresh-interval 1 \
    --counters "System.Runtime[gc-heap-size,threadpool-completed-items],Hugo.Go[waitgroup.additions,channel.select.latency]"
```

Key counters to watch (add additional `MeterName[CounterName]` pairs as your instrumentation grows):

- `Hugo.Go.waitgroup.additions` and `Hugo.Go.waitgroup.outstanding` highlight coordination pressure.
- `Hugo.Go.channel.select.latency` spikes when select loops stall.
- `System.Runtime.gc-heap-size` and `System.Runtime.cpu-usage` expose GC churn and CPU saturation.

Capture a short baseline (30–60 seconds) during idle and busy periods, then export to CSV for regression comparisons:

```bash
dotnet-counters collect --process-id <PID> --output counters.csv --duration 00:01:00
```

The Hugo scripts run the equivalent command and write `counters.csv` to a timestamped folder so you can diff baselines in source control.

## Step 3: Record EventPipe traces with `dotnet-trace`

Produce an EventPipe trace that you can open in PerfView, SpeedScope, or Visual Studio Profiler:

```bash
dotnet-trace collect \
    --process-id <PID> \
    --duration 00:00:30 \
    --providers "Microsoft-DotNETCore-SampleProfiler,System.Runtime:4:1"
```

- `Microsoft-DotNETCore-SampleProfiler` samples call stacks.
- `System.Runtime:4:1` emits GC, thread pool, and lock contention events. Increase the keyword mask for deeper detail when necessary.
- Add `System.Diagnostics.Metrics` with a `FilterSpecs` key/value (for example, `System.Diagnostics.Metrics:0:Informational:FilterSpecs=Meter=Hugo.Go`) to record selected meters alongside runtime events.
- Add [`System.Diagnostics.Metrics`](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace#collect) with a `FilterSpecs="Hugo.Go"` argument if you want to stream meter observations alongside runtime events.

After collection, convert traces for your preferred viewer:

```bash
dotnet-trace convert \
    --format speedscope \
    --output hugo.speedscope.json \
    hugo.nettrace
```

## Step 4: Automate recipes with `dotnet-monitor`

Use `dotnet-monitor` to standardize collection across environments:

```bash
dotnet monitor collect \
    --process-id <PID> \
    --metrics \
    --logs "LogLevel=Warning" \
    --trace "Duration=00:00:45" \
    --output ./artifacts/profiling
```

- The command writes metrics, traces, and logs to `./artifacts/profiling` with timestamped filenames.
- Combine `--process-filter from=Name,Value=YourWorker` to attach to services by name.
- Configure collection rules in `dotnet-monitor` by setting environment variables (for example, `CollectionRules__HugoSpike__Trigger__Type=AspNetRequestCount`). This allows automated sampling when throughput crosses a threshold.

The sample configuration in `tools/profiling-toolkit/collection-rules.sample.json` illustrates an `EventMeter` trigger over the `Hugo.Go` meter. Drop the file into your `dotnet monitor` deployment, adjust the `ProcessName` filter and egress directory, then launch:

```bash
dotnet monitor collect \
    --no-http-egress \
    --configurationFile ./tools/profiling-toolkit/collection-rules.sample.json
```

Run `dotnet monitor help collect` for all switches, including exporting straight to an OTLP backend or Azure Blob Storage.

### Automate baselines in CI with GitHub Actions

Ship reproducible baselines straight from CI by running the `Hugo Profiling Baseline` workflow that lives at `.github/workflows/profiling-baseline.yml`:

1. Navigate to **Actions ▸ Hugo Profiling Baseline ▸ Run workflow** and provide overrides when needed. The workflow defaults to `samples/Hugo.WorkerSample`, publishes it for `net10.0`, waits 10 seconds for warmup, and then calls `collect-baseline.sh` so the timestamped artifacts mirror local captures.
2. Customize collection knobs through the dispatch inputs:
    - `runDuration` and `traceDuration` control the underlying `dotnet-counters collect` and `dotnet-trace collect` durations.
    - `sampleProject`, `targetFramework`, and `dotnetVersion` let you target any Hugo-based worker or service in the repo.
    - `waitForWarmupSeconds` ensures background workloads have emitted metrics before sampling begins.
3. After completion, download the uploaded artifacts (`profiling-<run-id>` and `worker-log-<run-id>`) from the workflow run and check them into your baselines repo or attach them to an incident.

The workflow uses the same helper script and sets `ARTIFACT_ROOT=artifacts/profiling`, so you get consistent folder naming between local and CI scenarios. When profiling a different service, publish the corresponding project and update the `ProcessName` filter in `collection-rules.sample.json` to match the executable that runs in production.

## Step 5: Compare and share baselines

1. Commit a golden set of counters and traces for healthy builds in source control or artifact storage.
2. During regression investigations, re-run the recipes and diff metrics (for example, `git diff --stat artifacts/profiling`).
3. Share the `.nettrace` or SpeedScope JSON with the team; annotate findings such as long-lived goroutines or elevated GC pause ratios.

## Step 6: Inspect baselines with the analyzer

- Run `dotnet run --project tools/Hugo.ProfilingAnalyzer/Hugo.ProfilingAnalyzer.csproj -- artifacts/profiling/<timestamp>` to summarize counters after a collection. The CLI highlights wait-group leaks, channel latency spikes, GC pause ratios, and other heuristics derived from the captured metrics.
- Pass `--sort p95`, `--provider Hugo.Go`, or `--include-system` to focus the counter table on the most relevant signals. Combine `--findings-only` when you just want the heuristic verdict in CI logs.
- Supply `--speedscope artifacts/profiling/<timestamp>/trace.speedscope.json` (or drop the flag if the file sits next to `counters.csv`) to print the trace summary. Adjust `--trace-top` to display more or fewer hot frames in the Speedscope section.
- Invoke the analyzer on GitHub Actions artifacts by downloading the timestamped folder and pointing the command at the extracted directory (`counters.csv` is discovered automatically).

## Troubleshooting

- **Empty Hugo counters**: ensure `GoDiagnostics.Configure` executes before channel or wait-group creation, and confirm the process runs on .NET 6+ where EventPipe supports custom meters.
- **Permission denied**: run the CLI as an administrator or grant the `diagnostics` capability in containers (`--cap-add=SYS_PTRACE`).
- **High overhead**: lower the sampling duration or narrow providers (`--providers System.Runtime:4:0x8`) to focus on GC-only events.
- **dotnet-monitor cannot connect**: verify the diagnostic port path and disable HTTPS development certificates with `--tls false` when running locally.
- **dnx not found**: install the .NET 10 SDK preview or fall back to the traditional `dotnet tool install --global` workflow.

## Related guides

- [Publish metrics to OpenTelemetry](observe-with-opentelemetry.md)
- [Diagnostics reference](../reference/diagnostics.md)
- [.NET diagnostics overview](https://learn.microsoft.com/dotnet/core/diagnostics/)
