using BenchmarkDotNet.Attributes;

using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Go, BenchmarkCategories.Queues)]
public class TaskQueueFailureBenchmarks
{
    [Params(512)]
    public int ItemCount { get; set; }

    [Params(64)]
    public int Capacity { get; set; }

    [Benchmark]
    public async Task FailAndRequeueAsync()
    {
        int deadLettered = 0;
        await using var queue = new TaskQueue<int>(new TaskQueueOptions
        {
            Capacity = Capacity,
            LeaseDuration = TimeSpan.FromSeconds(30)
        },
        deadLetter: (ctx, _) =>
        {
            Interlocked.Increment(ref deadLettered);
            return ValueTask.CompletedTask;
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
            var rnd = new Random(17);
            var attempts = new Dictionary<long, int>();

            while (attempts.Count < ItemCount)
            {
                var lease = await queue.LeaseAsync().ConfigureAwait(false);
                var attempt = attempts.TryGetValue(lease.SequenceId, out var existing) ? existing + 1 : 1;
                attempts[lease.SequenceId] = attempt;

                // Simulate stochastic failures; requeue first two attempts, dead-letter on third.
                if (rnd.NextDouble() < 0.35 && attempt < 3)
                {
                    await lease.FailAsync(Error.From("transient", "error.taskqueue"), requeue: true).ConfigureAwait(false);
                }
                else if (attempt >= 3)
                {
                    await lease.FailAsync(Error.From("deadletter", "error.taskqueue"), requeue: false).ConfigureAwait(false);
                }
                else
                {
                    await lease.CompleteAsync().ConfigureAwait(false);
                }
            }
        });

        await Task.WhenAll(producer, consumer).ConfigureAwait(false);
        GC.KeepAlive(deadLettered);
    }
}
