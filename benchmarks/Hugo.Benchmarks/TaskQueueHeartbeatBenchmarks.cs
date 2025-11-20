using BenchmarkDotNet.Attributes;
using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Go, BenchmarkCategories.Queues)]
public class TaskQueueHeartbeatBenchmarks
{
    [Params(256)]
    public int LeaseCount { get; set; }

    [Params(50)]
    public int HeartbeatIntervalMs { get; set; }

    [Benchmark(Baseline = true)]
    public async Task HeartbeatThenCompleteAsync()
    {
        await using var queue = new TaskQueue<int>(new TaskQueueOptions
        {
            Capacity = LeaseCount,
            LeaseDuration = TimeSpan.FromMilliseconds(HeartbeatIntervalMs * 2),
            HeartbeatInterval = TimeSpan.FromMilliseconds(HeartbeatIntervalMs / 4)
        });

        for (var i = 0; i < LeaseCount; i++)
        {
            await queue.EnqueueAsync(i).ConfigureAwait(false);
        }

        var leases = new List<TaskQueueLease<int>>(LeaseCount);
        for (var i = 0; i < LeaseCount; i++)
        {
            leases.Add(await queue.LeaseAsync().ConfigureAwait(false));
        }

        // Heartbeat once, then complete.
        foreach (var lease in leases)
        {
            await lease.HeartbeatAsync().ConfigureAwait(false);
            BenchmarkWorkloads.SimulateLightCpuWork();
            await lease.CompleteAsync().ConfigureAwait(false);
        }
    }

    [Benchmark]
    public async Task ExpireAndRequeueAsync()
    {
        await using var queue = new TaskQueue<int>(new TaskQueueOptions
        {
            Capacity = LeaseCount,
            LeaseDuration = TimeSpan.FromMilliseconds(HeartbeatIntervalMs),
            MaxDeliveryAttempts = 2
        });

        for (var i = 0; i < LeaseCount; i++)
        {
            await queue.EnqueueAsync(i).ConfigureAwait(false);
        }

        // Take leases but never heartbeat; let monitor expire and requeue once, then complete.
        var firstRound = new List<TaskQueueLease<int>>(LeaseCount);
        for (var i = 0; i < LeaseCount; i++)
        {
            firstRound.Add(await queue.LeaseAsync().ConfigureAwait(false));
        }

        await Task.Delay(HeartbeatIntervalMs * 2).ConfigureAwait(false);

        for (var i = 0; i < LeaseCount; i++)
        {
            var lease = await queue.LeaseAsync().ConfigureAwait(false);
            await lease.CompleteAsync().ConfigureAwait(false);
        }

        GC.KeepAlive(firstRound);
    }
}
