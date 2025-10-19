using System;
using System.Collections.Generic;
using System.Linq;

namespace Hugo.Profiling;

public sealed record CounterReport(
    IReadOnlyList<CounterSummary> Counters,
    IReadOnlyList<string> ParseErrors,
    DateTime? FirstTimestamp,
    DateTime? LastTimestamp,
    int TotalRows,
    int ParsedRows)
{
    public IReadOnlyList<AnalyzerFinding> Findings { get; init; } = Array.Empty<AnalyzerFinding>();

    public TimeSpan Duration =>
        FirstTimestamp.HasValue && LastTimestamp.HasValue && LastTimestamp > FirstTimestamp
            ? LastTimestamp.Value - FirstTimestamp.Value
            : TimeSpan.Zero;

    public int SkippedRows => TotalRows - ParsedRows;

    public CounterSummary? FindCounter(string provider, string nameContains)
    {
        return Counters.FirstOrDefault(summary =>
            string.Equals(summary.Provider, provider, StringComparison.OrdinalIgnoreCase) &&
            summary.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
    }
}
