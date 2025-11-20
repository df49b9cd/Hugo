using BenchmarkDotNet.Attributes;

using Hugo;
using Hugo.Benchmarks.Time;

namespace Hugo.Benchmarks;

[SimpleJob]
[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Go, BenchmarkCategories.Queues)]
public class TaskQueueHeartbeatBenchmarks
{
    [Params(256)]
    public int LeaseCount { get; set; }

    [Params(50)]
    public int HeartbeatIntervalMs { get; set; }

    private FakeTimeProvider _timeProvider = null!;

    [GlobalSetup]
    public void Setup()
    {
        _timeProvider = new FakeTimeProvider();
    }

    [Benchmark(Baseline = true)]
    public async Task HeartbeatThenCompleteAsync()
    {
        await using var queue = new TaskQueue<int>(new TaskQueueOptions
        {
            Capacity = LeaseCount,
            LeaseDuration = TimeSpan.FromMilliseconds(HeartbeatIntervalMs * 2),
            HeartbeatInterval = TimeSpan.FromMilliseconds(HeartbeatIntervalMs / 4)
        }, _timeProvider);

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
            _timeProvider.Advance(TimeSpan.FromMilliseconds(HeartbeatIntervalMs / 2.0));
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
        }, _timeProvider);

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

        _timeProvider.Advance(TimeSpan.FromMilliseconds(HeartbeatIntervalMs * 2.0));

        for (var i = 0; i < LeaseCount; i++)
        {
            var lease = await queue.LeaseAsync().ConfigureAwait(false);
            await lease.CompleteAsync().ConfigureAwait(false);
        }

        GC.KeepAlive(firstRound);
    }
}
