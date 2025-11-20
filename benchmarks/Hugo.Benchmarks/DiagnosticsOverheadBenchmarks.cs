using System.Diagnostics;
using System.Diagnostics.Metrics;

using BenchmarkDotNet.Attributes;
using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Go, BenchmarkCategories.Diagnostics)]
public class DiagnosticsOverheadBenchmarks
{
    private bool _diagnosticsEnabled;

    [GlobalSetup]
    public void Setup()
    {
        _diagnosticsEnabled = string.Equals(
            Environment.GetEnvironmentVariable("HUGO_BENCH_DIAGNOSTICS"),
            "1",
            StringComparison.OrdinalIgnoreCase);

        if (_diagnosticsEnabled)
        {
            // Configure both meters and activity source to exercise the instrumentation path.
            var meter = new Meter("Hugo.Go.Bench", GoDiagnostics.InstrumentationVersion);
            var activitySource = GoDiagnostics.CreateActivitySource("Hugo.Go.Bench", GoDiagnostics.InstrumentationVersion, GoDiagnostics.TelemetrySchemaUrl);
            GoDiagnostics.Configure(meter, activitySource);
        }
    }

    [Benchmark(Baseline = true)]
    public async Task WaitGroup_NoDiagnosticsAsync()
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

    [Benchmark]
    public async Task WaitGroup_WithDiagnosticsAsync()
    {
        if (!_diagnosticsEnabled)
        {
            // Skip instrumentation when the diagnostics job is not active to avoid polluting baseline runs.
            await WaitGroup_NoDiagnosticsAsync();
            return;
        }

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
