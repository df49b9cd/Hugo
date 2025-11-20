using System;
using System.IO;

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;

namespace Hugo.Benchmarks;

internal static class BenchmarkConfig
{
    public static IConfig Create()
    {
        var artifactsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BenchmarkDotNet.Artifacts"));
        Directory.CreateDirectory(artifactsPath);

        return DefaultConfig.Instance
            .WithArtifactsPath(artifactsPath)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator | ConfigOptions.JoinSummary)
            .AddJob(Job.Default.WithId("default"))
            .AddJob(Job.ShortRun.WithId("quick"))
            .AddColumn(RankColumn.Arabic)
            .AddExporter(MarkdownExporter.GitHub)
            .AddDiagnoser(MemoryDiagnoser.Default);
    }
}
