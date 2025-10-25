using System.Text;

using Hugo.Profiling;

namespace Hugo.Tests.Profiling;

public sealed class CounterAnalyzerTests
{
    [Fact]
    public void Analyze_ComputesSummariesAndFindings()
    {
        const string csv = "Timestamp,Provider,Counter Name,Counter Type,Mean/Increment\n" +
                           "10/19/2025 21:56:35,System.Runtime,dotnet.process.cpu.count ({cpu}),Metric,8\n" +
                           "10/19/2025 21:56:36,Hugo.Go,waitgroup.outstanding ({count}),Metric,2\n" +
                           "10/19/2025 21:56:37,Hugo.Go,waitgroup.outstanding ({count}),Metric,0\n" +
                           "10/19/2025 21:56:36,System.Runtime,dotnet.gc.pause.time (s / 1 sec),Rate,0.05\n" +
                           "10/19/2025 21:56:37,System.Runtime,dotnet.gc.pause.time (s / 1 sec),Rate,0.05\n";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var report = CounterAnalyzer.Analyze(stream);

        Assert.Equal(TimeSpan.FromSeconds(2), report.Duration);
        Assert.Equal(5, report.TotalRows);
        Assert.Equal(5, report.ParsedRows);
        Assert.Empty(report.ParseErrors);

        var waitGroup = Assert.Single(report.Counters, static summary => summary.Provider == "Hugo.Go");
        Assert.Equal(2, waitGroup.Samples);
        Assert.Equal(2, waitGroup.Max);
        Assert.Equal(1, waitGroup.Mean);

        Assert.Contains(report.Findings, static finding =>
            finding.Severity == AnalyzerSeverity.Warning &&
            finding.Counter.Contains("waitgroup", StringComparison.OrdinalIgnoreCase));

        Assert.Contains(report.Findings, static finding =>
            finding.Counter.Contains("gc.pause.time", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Analyze_RecordsParseErrors()
    {
        const string csv = "Timestamp,Provider,Counter Name,Counter Type,Mean/Increment\n" +
                           "malformed-line-without-delimiters\n" +
                           "10/19/2025 21:56:35,System.Runtime,dotnet.process.cpu.count ({cpu}),Metric,invalid";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var report = CounterAnalyzer.Analyze(stream);

        Assert.Equal(2, report.TotalRows);
        Assert.Equal(0, report.ParsedRows);
        Assert.Equal(2, report.ParseErrors.Count);
        Assert.Empty(report.Counters);
    }
}
