using System.Threading.Channels;
using BenchmarkDotNet.Attributes;
using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
public class PrioritizedChannelBenchmarks
{
    private const int ItemCount = 4096;
    private const int CapacityPerLevel = 64;

    [Params(3, 5)]
    public int PriorityLevels { get; set; }

    [Params(false, true)]
    public bool UseBoundedCapacity { get; set; }

    private int[]? _priorities;

    [GlobalSetup]
    public void Initialize()
    {
        var priorities = new int[ItemCount];
        for (var i = 0; i < priorities.Length; i++)
        {
            priorities[i] = i % PriorityLevels;
        }

        _priorities = priorities;
    }

    [Benchmark]
    public Task PrioritizedChannelAsync() => ExecutePrioritizedAsync();

    [Benchmark]
    public Task StandardBoundedChannelAsync() => ExecuteStandardAsync(bounded: true);

    [Benchmark]
    public Task StandardUnboundedChannelAsync() => ExecuteStandardAsync(bounded: false);

    private async Task ExecutePrioritizedAsync()
    {
        if (_priorities is null)
            throw new InvalidOperationException("Priorities were not initialized.");

        var options = new PrioritizedChannelOptions
        {
            PriorityLevels = PriorityLevels,
            CapacityPerLevel = UseBoundedCapacity ? CapacityPerLevel : null,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        };

        var channel = Go.MakeChannel<int>(options);
        var writer = channel.PrioritizedWriter;
        var reader = channel.PrioritizedReader;

        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < ItemCount; i++)
            {
                var priority = _priorities[i];
                await writer.WriteAsync(i, priority).ConfigureAwait(false);
            }

            writer.TryComplete();
        });

        var consumer = Task.Run(async () =>
        {
            var total = 0L;
            for (var i = 0; i < ItemCount; i++)
            {
                var value = await reader.ReadAsync().ConfigureAwait(false);
                BenchmarkWorkloads.SimulateLightCpuWork();
                total += value;
            }

            GC.KeepAlive(total);
        });

        await Task.WhenAll(producer, consumer).ConfigureAwait(false);
    }

    private async Task ExecuteStandardAsync(bool bounded)
    {
        if (_priorities is null)
            throw new InvalidOperationException("Priorities were not initialized.");

        Channel<int> channel;
        if (bounded && UseBoundedCapacity)
        {
            channel = Go.MakeChannel<int>(capacity: CapacityPerLevel * PriorityLevels, fullMode: BoundedChannelFullMode.Wait, singleReader: true, singleWriter: false);
        }
        else if (bounded)
        {
            channel = Channel.CreateBounded<int>(new BoundedChannelOptions(CapacityPerLevel * PriorityLevels)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        }
        else
        {
            channel = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < ItemCount; i++)
            {
                await channel.Writer.WriteAsync(i).ConfigureAwait(false);
            }

            channel.Writer.TryComplete();
        });

        var consumer = Task.Run(async () =>
        {
            var total = 0L;
            for (var i = 0; i < ItemCount; i++)
            {
                var value = await channel.Reader.ReadAsync().ConfigureAwait(false);
                BenchmarkWorkloads.SimulateLightCpuWork();
                total += value;
            }

            GC.KeepAlive(total);
        });

        await Task.WhenAll(producer, consumer).ConfigureAwait(false);
    }
}
