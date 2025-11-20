using System.Diagnostics;
using System.Diagnostics.Metrics;

using BenchmarkDotNet.Attributes;

using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Go, BenchmarkCategories.Diagnostics)]
public class DiagnosticsOverheadBenchmarks
{
    [Params(false, true)]
    public bool EnableDiagnostics { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        if (EnableDiagnostics)
        {
            // Configure both meters and activity sources per run so we isolate overhead.
            var meter = new Meter("Hugo.Go.Bench", GoDiagnostics.InstrumentationVersion);
            var activitySource = GoDiagnostics.CreateActivitySource("Hugo.Go.Bench", GoDiagnostics.InstrumentationVersion, GoDiagnostics.TelemetrySchemaUrl);
            GoDiagnostics.Configure(meter, activitySource);
        }
    }

    [Benchmark]
    public async Task WaitGroupAsync()
    {
        var wg = new WaitGroup();
        for (var i = 0; i < 32; i++)
        {
            wg.Go(static async () =>
            {
                BenchmarkWorkloads.SimulateLightCpuWork();
                await Task.Yield();
            });
        }

        await wg.WaitAsync();
    }
}
