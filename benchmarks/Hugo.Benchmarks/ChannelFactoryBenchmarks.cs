using System.Threading.Channels;

using BenchmarkDotNet.Attributes;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
internal class ChannelFactoryBenchmarks
{
    private const int Capacity = 256;
    private const int ItemCount = 2048;

    [Params(false, true)]
    public bool SingleReader { get; set; }

    [Params(false, true)]
    public bool SingleWriter { get; set; }

    [Benchmark]
    public Task GoMakeUnboundedAsync() => RunStandardAsync(() => Go.MakeChannel<int>(singleReader: SingleReader, singleWriter: SingleWriter));

    [Benchmark]
    public Task GoMakeBoundedAsync() => RunStandardAsync(() => Go.MakeChannel<int>(capacity: Capacity, fullMode: BoundedChannelFullMode.Wait, singleReader: SingleReader, singleWriter: SingleWriter));

    [Benchmark]
    public Task NativeBoundedAsync() => RunStandardAsync(() => Channel.CreateBounded<int>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = SingleReader,
        SingleWriter = SingleWriter
    }));

    [Benchmark]
    public Task NativeUnboundedAsync() => RunStandardAsync(() => Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = SingleReader,
        SingleWriter = SingleWriter
    }));

    [Benchmark]
    public Task GoMakePrioritizedAsync() => RunPrioritizedAsync(() => Go.MakePrioritizedChannel<int>(priorityLevels: 3, capacityPerLevel: Capacity / 3, singleReader: SingleReader, singleWriter: SingleWriter));

    private static async Task RunStandardAsync(Func<Channel<int>> factory)
    {
        var channel = factory();
        var writer = channel.Writer;
        var reader = channel.Reader;

        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < ItemCount; i++)
            {
                await writer.WriteAsync(i).ConfigureAwait(false);
            }

            writer.TryComplete();
        });

        var consumer = Task.Run(async () =>
        {
            var total = 0L;
            for (var i = 0; i < ItemCount; i++)
            {
                var value = await reader.ReadAsync().ConfigureAwait(false);
                total += value;
            }

            GC.KeepAlive(total);
        });

        await Task.WhenAll(producer, consumer).ConfigureAwait(false);
    }

    private static async Task RunPrioritizedAsync(Func<PrioritizedChannel<int>> factory)
    {
        var channel = factory();
        var writer = channel.PrioritizedWriter;
        var reader = channel.PrioritizedReader;

        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < ItemCount; i++)
            {
                await writer.WriteAsync(i, priority: i % channel.PriorityLevels).ConfigureAwait(false);
            }

            writer.TryComplete();
        });

        var consumer = Task.Run(async () =>
        {
            var total = 0L;
            for (var i = 0; i < ItemCount; i++)
            {
                var value = await reader.ReadAsync().ConfigureAwait(false);
                total += value;
            }

            GC.KeepAlive(total);
        });

        await Task.WhenAll(producer, consumer).ConfigureAwait(false);
    }
}
