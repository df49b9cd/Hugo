# Hugo Profiling Toolkit Assets

This folder contains automation helpers that accompany the [profiling toolkit how-to](../../docs/how-to/profiling-toolkit.md).

## Scripts

- `collect-baseline.sh`
  - Bash helper for macOS/Linux.
  - Captures `dotnet-counters collect` output and a `dotnet-trace collect` session into `artifacts/profiling/<timestamp>`.
  - Falls back to [`dnx dotnet-counters`](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-counters#install) and [`dnx dotnet-trace`](https://learn.microsoft.com/dotnet/core/diagnostics/dotnet-trace#install) when the global tools are not already installed.

- `collect-baseline.ps1`
  - PowerShell equivalent for Windows hosts.

Both scripts accept `--pid`/`-Pid` or `--name`/`-Name` targets plus duration, counter, and profile overrides. Examples:

```bash
./tools/profiling-toolkit/collect-baseline.sh --pid 4242 --duration 00:02:00 --trace-duration 00:00:45
```

```powershell
pwsh ./tools/profiling-toolkit/collect-baseline.ps1 -Name WorkerService -Profile gc-verbose -SkipCounters
```

> [!NOTE]
> Mark the bash script executable (`chmod +x tools/profiling-toolkit/collect-baseline.sh`) before first use.

## Collection rule template

The `collection-rules.sample.json` file seeds a `dotnet monitor` configuration that:

- Watches the `Hugo.Go` meter for elevated `waitgroup.outstanding` values.
- Captures a CPU trace and a warning-level log slice when the threshold is breached.
- Stores artifacts locally using the `FileSystem` egress provider.

Update the process filter and egress paths before applying the configuration (
`dotnet monitor collect --no-http-egress --configurationFile collection-rules.sample.json`).

For additional triggers and targets, consult the upstream [`dotnet monitor` collection rules documentation](https://github.com/dotnet/dotnet-monitor/tree/main/documentation/collectionrules).
