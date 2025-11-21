using BenchmarkDotNet.Attributes;

using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Go, BenchmarkCategories.Timers)]
public class GoTickerBenchmarks
{
    [Params(64, 256)]
    public int TickCount { get; set; }

    [Params(1)] // milliseconds
    public int PeriodMs { get; set; }

    [Benchmark(Baseline = true)]
    public async ValueTask GoTickerAsync()
    {
        var period = TimeSpan.FromMilliseconds(PeriodMs);
        await using var ticker = Go.NewTicker(period, TimeProvider.System);

        for (var i = 0; i < TickCount; i++)
        {
            await ticker.Reader.ReadAsync().ConfigureAwait(false);
        }
    }

    [Benchmark]
    public async ValueTask PeriodicTimerAsync()
    {
        var period = TimeSpan.FromMilliseconds(PeriodMs);
        using var timer = new PeriodicTimer(period);

        for (var i = 0; i < TickCount; i++)
        {
            if (!await timer.WaitForNextTickAsync().ConfigureAwait(false))
            {
                break;
            }
        }
    }
}
