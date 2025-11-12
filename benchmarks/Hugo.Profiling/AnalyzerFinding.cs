namespace Hugo.Profiling;

public enum AnalyzerSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public sealed record AnalyzerFinding(AnalyzerSeverity Severity, string Counter, string Message);
