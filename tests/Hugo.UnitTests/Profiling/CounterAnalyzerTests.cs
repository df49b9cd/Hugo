using System.Text;

using Hugo.Profiling;

using Shouldly;

namespace Hugo.Tests.Profiling;

public sealed class CounterAnalyzerTests
{
    [Fact(Timeout = 15_000)]
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

        report.Duration.ShouldBe(TimeSpan.FromSeconds(2));
        report.TotalRows.ShouldBe(5);
        report.ParsedRows.ShouldBe(5);
        report.ParseErrors.ShouldBeEmpty();

        var waitGroup = report.Counters.Single(static summary => summary.Provider == "Hugo.Go");
        waitGroup.Samples.ShouldBe(2);
        waitGroup.Max.ShouldBe(2);
        waitGroup.Mean.ShouldBe(1);

        report.Findings.ShouldContain(static finding =>
            finding.Severity == AnalyzerSeverity.Warning &&
            finding.Counter.Contains("waitgroup", StringComparison.OrdinalIgnoreCase));

        report.Findings.ShouldContain(static finding =>
            finding.Counter.Contains("gc.pause.time", StringComparison.OrdinalIgnoreCase));
    }

    [Fact(Timeout = 15_000)]
    public void Analyze_RecordsParseErrors()
    {
        const string csv = "Timestamp,Provider,Counter Name,Counter Type,Mean/Increment\n" +
                           "malformed-line-without-delimiters\n" +
                           "10/19/2025 21:56:35,System.Runtime,dotnet.process.cpu.count ({cpu}),Metric,invalid";

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var report = CounterAnalyzer.Analyze(stream);

        report.TotalRows.ShouldBe(2);
        report.ParsedRows.ShouldBe(0);
        report.ParseErrors.Count.ShouldBe(2);
        report.Counters.ShouldBeEmpty();
    }
}
