#!/usr/bin/env bash
set -euo pipefail

print_help() {
    cat <<'EOF'
Usage: collect-baseline.sh [options]

Capture counters and traces from a running .NET process to build a Hugo profiling baseline.

Required options:
  -p, --pid <pid>          Process ID to inspect.
  -n, --name <name>        Process name to inspect (alternative to --pid).

Optional:
  --duration <HH:MM:SS>    Duration for dotnet-counters collect (default 00:01:00).
  --trace-duration <HH:MM:SS>
                           Duration for dotnet-trace collect (default 00:00:30).
  --refresh-interval <sec> Refresh interval for counters (default 1 second).
  --output <path>          Output directory (default artifacts/profiling/<timestamp>).
  --counters <list>        Comma-separated counters (default System.Runtime,Hugo.Go).
  --profile <name>         dotnet-trace profile (default cpu-sampling).
  --providers <list>       Additional dotnet-trace providers string.
  --skip-counters          Skip dotnet-counters collection.
  --skip-trace             Skip dotnet-trace collection.
  -h, --help               Show this help message.

If global tools are not installed, the script automatically falls back to the
one-shot 'dnx <tool>' activation available in .NET 10 SDK builds.
EOF
}

require_tool() {
    if ! command -v dotnet >/dev/null 2>&1; then
        echo "error: dotnet CLI is required" >&2
        exit 1
    fi
}

run_tool() {
    local tool="$1"
    shift

    if command -v "$tool" >/dev/null 2>&1; then
        "$tool" "$@"
    elif command -v dnx >/dev/null 2>&1; then
        dnx "$tool" "$@"
    else
        echo "error: $tool is not available. Install via 'dotnet tool install --global $tool' or ensure the dnx launcher is on PATH." >&2
        exit 1
    fi
}

require_tool

pid=""
name=""
duration="00:01:00"
trace_duration="00:00:30"
refresh_interval="1"
output_root=""
counters="System.Runtime,Hugo.Go"
profile="cpu-sampling"
providers=""
skip_counters=0
skip_trace=0

while [[ $# -gt 0 ]]; do
    case "$1" in
        -p|--pid)
            pid="$2"
            shift 2
            ;;
        -n|--name)
            name="$2"
            shift 2
            ;;
        --duration)
            duration="$2"
            shift 2
            ;;
        --trace-duration)
            trace_duration="$2"
            shift 2
            ;;
        --refresh-interval)
            refresh_interval="$2"
            shift 2
            ;;
        --output)
            output_root="$2"
            shift 2
            ;;
        --counters)
            counters="$2"
            shift 2
            ;;
        --profile)
            profile="$2"
            shift 2
            ;;
        --providers)
            providers="$2"
            shift 2
            ;;
        --skip-counters)
            skip_counters=1
            shift
            ;;
        --skip-trace)
            skip_trace=1
            shift
            ;;
        -h|--help)
            print_help
            exit 0
            ;;
        *)
            echo "error: unknown option '$1'" >&2
            print_help
            exit 1
            ;;
    esac
done

if [[ -n "$pid" && -n "$name" ]]; then
    echo "error: specify either --pid or --name, not both" >&2
    exit 1
fi

if [[ -z "$pid" && -z "$name" ]]; then
    echo "error: a target --pid or --name is required" >&2
    exit 1
fi

timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
if [[ -z "$output_root" ]]; then
    output_root="artifacts/profiling/$timestamp"
else
    output_root="$output_root/$timestamp"
fi

mkdir -p "$output_root"

target_args=()
if [[ -n "$pid" ]]; then
    target_args=(--process-id "$pid")
else
    target_args=(--name "$name")
fi

echo "[collect-baseline] Output folder: $output_root"

echo "[collect-baseline] Target: ${target_args[*]}"

if [[ $skip_counters -eq 0 ]]; then
    counters_file="$output_root/counters.csv"
    echo "[collect-baseline] Running dotnet-counters collect for $duration"
    run_tool dotnet-counters --yes collect "${target_args[@]}" \
        --refresh-interval "$refresh_interval" \
        --format csv \
        --output "$counters_file" \
        --duration "$duration" \
        --counters "$counters"
    echo "[collect-baseline] Counters saved to $counters_file"
fi

if [[ $skip_trace -eq 0 ]]; then
    trace_file="$output_root/hugo.nettrace"
    speedscope_file="$output_root/hugo.speedscope.json"

    echo "[collect-baseline] Running dotnet-trace collect for $trace_duration"
    trace_cmd=(dotnet-trace collect "${target_args[@]}" --duration "$trace_duration" --profile "$profile" --output "$trace_file")
    if [[ -n "$providers" ]]; then
        trace_cmd+=(--providers "$providers")
    fi
    run_tool "${trace_cmd[@]}"

    echo "[collect-baseline] Converting trace to speedscope"
    run_tool dotnet-trace --yes convert "$trace_file" --format speedscope --output "$speedscope_file"
    echo "[collect-baseline] Trace saved to $trace_file and $speedscope_file"
fi

echo "[collect-baseline] Complete. Review artifacts in $output_root"
