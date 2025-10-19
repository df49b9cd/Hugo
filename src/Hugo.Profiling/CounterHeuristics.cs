using System;
using System.Collections.Generic;
using System.Linq;

namespace Hugo.Profiling;

internal static class CounterHeuristics
{
    public static IReadOnlyList<AnalyzerFinding> Evaluate(CounterReport report)
    {
        if (report.Counters.Count == 0)
        {
            return Array.Empty<AnalyzerFinding>();
        }

        var findings = new List<AnalyzerFinding>();

        AddWaitGroupFindings(report, findings);
        AddChannelLatencyFindings(report, findings);
        AddThreadPoolFindings(report, findings);
        AddGcPauseFindings(report, findings);
        AddLockContentionFindings(report, findings);
        AddCpuFindings(report, findings);
        AddAllocationFinding(report, findings);

        return findings
            .OrderByDescending(finding => finding.Severity)
            .ThenBy(finding => finding.Counter, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddWaitGroupFindings(CounterReport report, ICollection<AnalyzerFinding> findings)
    {
        var series = report.FindCounter("Hugo.Go", "waitgroup.outstanding");
        if (series == null || series.Max <= 0)
        {
            return;
        }

        var severity = series.Max >= 5 ? AnalyzerSeverity.Critical : AnalyzerSeverity.Warning;
        var message = $"WaitGroup outstanding peaked at {series.Max:0.##}. Ensure Add and Done calls are balanced.";
        findings.Add(new AnalyzerFinding(severity, Format(series), message));
    }

    private static void AddChannelLatencyFindings(CounterReport report, ICollection<AnalyzerFinding> findings)
    {
        var series = report.FindCounter("Hugo.Go", "channel.select.latency");
        if (series == null || double.IsNaN(series.P95))
        {
            return;
        }

        const double warningThresholdSeconds = 0.010; // 10 ms
        const double criticalThresholdSeconds = 0.050; // 50 ms

        if (series.P95 < warningThresholdSeconds)
        {
            return;
        }

        var severity = series.P95 >= criticalThresholdSeconds ? AnalyzerSeverity.Critical : AnalyzerSeverity.Warning;
        var message = $"Channel select P95 latency reached {series.P95 * 1_000:0.##} ms. Investigate slow consumers or contention.";
        findings.Add(new AnalyzerFinding(severity, Format(series), message));
    }

    private static void AddThreadPoolFindings(CounterReport report, ICollection<AnalyzerFinding> findings)
    {
        var queueSeries = report.FindCounter("System.Runtime", "thread_pool.queue.length");
        if (queueSeries != null && queueSeries.Max >= 1)
        {
            var severity = queueSeries.Max >= 10 ? AnalyzerSeverity.Warning : AnalyzerSeverity.Info;
            var message = $"Thread pool queue length peaked at {queueSeries.Max:0.##}. Consider diagnosing blocked work or increasing parallelism.";
            findings.Add(new AnalyzerFinding(severity, Format(queueSeries), message));
        }
    }

    private static void AddGcPauseFindings(CounterReport report, ICollection<AnalyzerFinding> findings)
    {
        var gcPauseSeries = report.FindCounter("System.Runtime", "gc.pause.time");
        if (gcPauseSeries == null || report.Duration <= TimeSpan.Zero)
        {
            return;
        }

        var pauseShare = gcPauseSeries.Sum / report.Duration.TotalSeconds;
        if (pauseShare >= 0.20)
        {
            var message = $"GC pauses consumed {(pauseShare * 100):0.##}% of the baseline window.";
            findings.Add(new AnalyzerFinding(AnalyzerSeverity.Critical, Format(gcPauseSeries), message));
        }
        else if (pauseShare >= 0.05)
        {
            var message = $"GC pauses consumed {(pauseShare * 100):0.##}% of the baseline window.";
            findings.Add(new AnalyzerFinding(AnalyzerSeverity.Warning, Format(gcPauseSeries), message));
        }
    }

    private static void AddLockContentionFindings(CounterReport report, ICollection<AnalyzerFinding> findings)
    {
        var lockContentionSeries = report.FindCounter("System.Runtime", "monitor.lock_contentions");
        if (lockContentionSeries == null || lockContentionSeries.Sum < 1)
        {
            return;
        }

        var severity = lockContentionSeries.Sum >= 10 ? AnalyzerSeverity.Warning : AnalyzerSeverity.Info;
        var message = $"Observed approximately {lockContentionSeries.Sum:0.##} lock contentions during the capture.";
        findings.Add(new AnalyzerFinding(severity, Format(lockContentionSeries), message));
    }

    private static void AddCpuFindings(CounterReport report, ICollection<AnalyzerFinding> findings)
    {
        if (report.Duration <= TimeSpan.Zero)
        {
            return;
        }

        var cpuCounters = report.Counters.Where(series =>
            string.Equals(series.Provider, "System.Runtime", StringComparison.OrdinalIgnoreCase) &&
            series.Name.StartsWith("dotnet.process.cpu.time", StringComparison.OrdinalIgnoreCase)).ToList();

        if (cpuCounters.Count == 0)
        {
            return;
        }

        var cpuSeconds = cpuCounters.Sum(series => series.Sum);
        if (cpuSeconds <= 0)
        {
            return;
        }

        var cpuCountSeries = report.FindCounter("System.Runtime", "process.cpu.count");
        var cpuCount = cpuCountSeries?.Latest ?? Environment.ProcessorCount;
        if (cpuCount < 1)
        {
            cpuCount = 1;
        }

        var avgCpuPercent = cpuSeconds / (report.Duration.TotalSeconds * cpuCount) * 100;
        if (double.IsNaN(avgCpuPercent) || avgCpuPercent <= 0)
        {
            return;
        }

        AnalyzerSeverity severity;
        if (avgCpuPercent >= 95)
        {
            severity = AnalyzerSeverity.Critical;
        }
        else if (avgCpuPercent >= 80)
        {
            severity = AnalyzerSeverity.Warning;
        }
        else if (avgCpuPercent >= 65)
        {
            severity = AnalyzerSeverity.Info;
        }
        else
        {
            return;
        }

        var message = $"Average CPU utilization ~{avgCpuPercent:0.#}% across {cpuCount:0.#} logical processors.";
        findings.Add(new AnalyzerFinding(severity, "System.Runtime/dotnet.process.cpu.time", message));
    }

    private static void AddAllocationFinding(CounterReport report, ICollection<AnalyzerFinding> findings)
    {
        var allocationSeries = report.FindCounter("System.Runtime", "gc.heap.total_allocated");
        if (allocationSeries == null || allocationSeries.Sum <= 0)
        {
            return;
        }

        var allocationMegabytes = allocationSeries.Sum / (1024 * 1024);
        var durationSeconds = Math.Max(report.Duration.TotalSeconds, 0.0001);
        var message = $"Estimated allocations ~{allocationMegabytes:0.##} MB over {durationSeconds:0.##} seconds.";
        findings.Add(new AnalyzerFinding(AnalyzerSeverity.Info, Format(allocationSeries), message));
    }

    private static string Format(CounterSummary summary) => $"{summary.Provider}/{summary.Name}";
}
