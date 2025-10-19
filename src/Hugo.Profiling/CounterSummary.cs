using System;

namespace Hugo.Profiling;

public sealed record CounterSummary(
    string Provider,
    string Name,
    string Type,
    int Samples,
    double Min,
    double Max,
    double Mean,
    double P95,
    double P99,
    double Latest,
    double Sum,
    DateTime FirstTimestamp,
    DateTime LastTimestamp)
{
    public TimeSpan Duration => Samples > 1 ? LastTimestamp - FirstTimestamp : TimeSpan.Zero;
}
