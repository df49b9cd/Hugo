<#!
.SYNOPSIS
    Capture counters and traces from a running .NET process to create a Hugo profiling baseline.

.DESCRIPTION
    Wraps dotnet-counters and dotnet-trace (or their dnx one-shot variants) to collect
    repeatable diagnostic artifacts. Output is written to artifacts/profiling/<timestamp>
    by default.

.PARAMETER Pid
    Process ID to inspect.

.PARAMETER Name
    Process name to inspect (alternative to Pid).

.PARAMETER Duration
    dotnet-counters collection duration (HH:MM:SS). Defaults to 00:01:00.

.PARAMETER TraceDuration
    dotnet-trace capture duration (HH:MM:SS). Defaults to 00:00:30.

.PARAMETER RefreshInterval
    Refresh interval in seconds for dotnet-counters collect. Defaults to 1.

.PARAMETER Output
    Root folder that will receive timestamped artifacts.

.PARAMETER Counters
    Comma-separated counter list. Defaults to "System.Runtime,Hugo.Go".

.PARAMETER Profile
    dotnet-trace capture profile. Defaults to "cpu-sampling".

.PARAMETER Providers
    Additional dotnet-trace provider string.

.PARAMETER SkipCounters
    Skip dotnet-counters collection.

.PARAMETER SkipTrace
    Skip dotnet-trace collection.
#>
[CmdletBinding(DefaultParameterSetName = 'Target')]
param(
    [Parameter(Mandatory = $false, ParameterSetName = 'Pid')]
    [int]
    $Pid,

    [Parameter(Mandatory = $false, ParameterSetName = 'Name')]
    [string]
    $Name,

    [string]
    $Duration = '00:01:00',

    [string]
    $TraceDuration = '00:00:30',

    [int]
    $RefreshInterval = 1,

    [string]
    $Output,

    [string]
    $Counters = 'System.Runtime,Hugo.Go',

    [string]
    $Profile = 'cpu-sampling',

    [string]
    $Providers,

    [switch]
    $SkipCounters,

    [switch]
    $SkipTrace
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet CLI is required to run this script.'
}

if (-not $Pid -and -not $Name) {
    throw 'Specify either -Pid or -Name.'
}

if ($Pid -and $Name) {
    throw 'Specify only one of -Pid or -Name.'
}

$timestamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
if ([string]::IsNullOrEmpty($Output)) {
    $Output = Join-Path -Path 'artifacts/profiling' -ChildPath $timestamp
}
else {
    $Output = Join-Path -Path $Output -ChildPath $timestamp
}

if (-not (Test-Path -Path $Output)) {
    New-Item -ItemType Directory -Path $Output | Out-Null
}

Write-Host "[collect-baseline] Output folder: $Output"

$targetArgs = if ($Pid) { @('--process-id', $Pid) } else { @('--name', $Name) }
Write-Host "[collect-baseline] Target: $($targetArgs -join ' ')"

function Invoke-Tool {
    param(
        [Parameter(Mandatory = $true)]
        [string]
        $Tool,

        [Parameter(Mandatory = $false)]
        [string[]]
        $Arguments
    )

    if (Get-Command $Tool -ErrorAction SilentlyContinue) {
        & $Tool @Arguments
    }
    elseif (Get-Command dnx -ErrorAction SilentlyContinue) {
        & dnx $Tool @Arguments
    }
    else {
        throw "Tool '$Tool' not found. Install via 'dotnet tool install --global $Tool' or ensure the dnx launcher is on PATH."
    }
}

if (-not $SkipCounters) {
    $countersFile = Join-Path -Path $Output -ChildPath 'counters.csv'
    Write-Host "[collect-baseline] Running dotnet-counters collect for $Duration"
    $counterArgs = @('collect') + $targetArgs + @('--refresh-interval', $RefreshInterval.ToString(), '--format', 'csv', '--output', $countersFile, '--duration', $Duration, '--counters', $Counters)
    Invoke-Tool -Tool 'dotnet-counters' -Arguments $counterArgs
    Write-Host "[collect-baseline] Counters saved to $countersFile"
}

if (-not $SkipTrace) {
    $traceFile = Join-Path -Path $Output -ChildPath 'hugo.nettrace'
    $speedscopeFile = Join-Path -Path $Output -ChildPath 'hugo.speedscope.json'

    Write-Host "[collect-baseline] Running dotnet-trace collect for $TraceDuration"
    $traceArgs = @('collect') + $targetArgs + @('--duration', $TraceDuration, '--profile', $Profile, '--output', $traceFile)
    if ($Providers) {
        $traceArgs += @('--providers', $Providers)
    }
    Invoke-Tool -Tool 'dotnet-trace' -Arguments $traceArgs

    Write-Host '[collect-baseline] Converting trace to speedscope'
    $convertArgs = @($traceFile, '--format', 'speedscope', '--output', $speedscopeFile)
    Invoke-Tool -Tool 'dotnet-trace' -Arguments (@('convert') + $convertArgs)
    Write-Host "[collect-baseline] Trace saved to $traceFile and $speedscopeFile"
}

Write-Host "[collect-baseline] Complete. Review artifacts in $Output"
