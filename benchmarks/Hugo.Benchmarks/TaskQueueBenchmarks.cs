using System.Threading.Channels;

using BenchmarkDotNet.Attributes;

using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Go, BenchmarkCategories.Queues)]
public class TaskQueueBenchmarks
{
    [Params(1024, 4096)]
    public int ItemCount { get; set; }

    [Params(128)]
    public int Capacity { get; set; }

    [Benchmark(Baseline = true)]
    public async Task TaskQueue_LeaseAndCompleteAsync()
    {
        await using var queue = new TaskQueue<int>(new TaskQueueOptions
        {
            Capacity = Capacity,
            LeaseDuration = TimeSpan.FromSeconds(30)
        });

        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < ItemCount; i++)
            {
                await queue.EnqueueAsync(i).ConfigureAwait(false);
            }
        });

        var consumer = Task.Run(async () =>
        {
            long sum = 0;
            for (var i = 0; i < ItemCount; i++)
            {
                var lease = await queue.LeaseAsync().ConfigureAwait(false);
                sum += lease.Value;
                await lease.CompleteAsync().ConfigureAwait(false);
            }

            GC.KeepAlive(sum);
        });

        await Task.WhenAll(producer, consumer).ConfigureAwait(false);
    }

    [Benchmark]
    public async Task BoundedChannel_ReadWriteAsync()
    {
        var channel = Channel.CreateBounded<int>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false
        });

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
            long sum = 0;
            for (var i = 0; i < ItemCount; i++)
            {
                var value = await channel.Reader.ReadAsync().ConfigureAwait(false);
                sum += value;
            }

            GC.KeepAlive(sum);
        });

        await Task.WhenAll(producer, consumer).ConfigureAwait(false);
    }
}
