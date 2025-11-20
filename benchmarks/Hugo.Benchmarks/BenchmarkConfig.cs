using System;
using System.IO;

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Environments;

namespace Hugo.Benchmarks;

internal static class BenchmarkConfig
{
    public static IConfig Create()
    {
        var artifactsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BenchmarkDotNet.Artifacts"));
        Directory.CreateDirectory(artifactsPath);

        var diagnosticsJob = Job.Default
            .WithId("diag")
            .WithEnvironmentVariables(new[] { new EnvironmentVariable("HUGO_BENCH_DIAGNOSTICS", "1") });

        return DefaultConfig.Instance
            .WithArtifactsPath(artifactsPath)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator | ConfigOptions.JoinSummary)
            .AddJob(Job.Default.WithId("default"))
            .AddJob(Job.ShortRun.WithId("quick"))
            .AddJob(diagnosticsJob)
            .AddColumn(RankColumn.Arabic)
            .AddExporter(MarkdownExporter.GitHub)
            .AddDiagnoser(MemoryDiagnoser.Default);
    }
}
