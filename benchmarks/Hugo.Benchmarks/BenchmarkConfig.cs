using System;
using System.IO;

using BenchmarkDotNet.Columns;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Environments;
using BenchmarkDotNet.Exporters;
using BenchmarkDotNet.Jobs;

namespace Hugo.Benchmarks;

internal static class BenchmarkConfig
{
    public static IConfig Create()
    {
        var artifactsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "BenchmarkDotNet.Artifacts"));
        Directory.CreateDirectory(artifactsPath);

        var serverDefault = Job.Default.WithId("default").WithGcServer(true);
        var serverQuick = Job.ShortRun.WithId("quick").WithGcServer(true);
        var diagnosticsJob = Job.Default
            .WithId("diag")
            .WithGcServer(true)
            .WithEnvironmentVariables(new[] { new EnvironmentVariable("HUGO_BENCH_DIAGNOSTICS", "1") });

        return DefaultConfig.Instance
            .WithArtifactsPath(artifactsPath)
            .WithOptions(ConfigOptions.DisableOptimizationsValidator | ConfigOptions.JoinSummary)
            .AddJob(serverDefault)
            .AddJob(serverQuick)
            .AddJob(diagnosticsJob)
            .AddColumn(RankColumn.Arabic)
            .AddExporter(MarkdownExporter.GitHub)
            .AddDiagnoser(MemoryDiagnoser.Default);
    }
}
