using System.Threading.RateLimiting;
using Shouldly;

using Hugo.TaskQueues.Backpressure;

using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests;

[Collection("TaskQueueConcurrency")]
public class TaskQueueBackpressureIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async Task LimiterSelector_ShouldFlipDuringBackpressure()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Capacity = 32 }, provider);
        await using var monitor = new TaskQueueBackpressureMonitor<int>(queue, new TaskQueueBackpressureMonitorOptions
        {
            HighWatermark = 4,
            LowWatermark = 2,
            Cooldown = TimeSpan.FromMilliseconds(5)
        });

        var unthrottled = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 32,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
        var throttled = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
        using var listener = new BackpressureAwareRateLimiter(unthrottled, throttled, disposeUnthrottledLimiter: true, disposeBackpressureLimiter: true);
        using var subscription = monitor.RegisterListener(listener);

        for (var i = 0; i < 8; i++)
        {
            await queue.EnqueueAsync(i, TestContext.Current.CancellationToken);
        }

        await EventuallyAsync(() => ReferenceEquals(listener.LimiterSelector(), throttled));

        var consumer = Task.Run(async () =>
        {
            for (var i = 0; i < 8; i++)
            {
                var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
                await lease.CompleteAsync(TestContext.Current.CancellationToken);
                provider.Advance(TimeSpan.FromMilliseconds(3));
            }
        }, TestContext.Current.CancellationToken);

        await consumer;
        await EventuallyAsync(() => ReferenceEquals(listener.LimiterSelector(), unthrottled));
    }

    [Fact(Timeout = 15_000)]
    public async Task DiagnosticsListener_ShouldStreamOrderedSignals()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<string>(new TaskQueueOptions { Capacity = 16 }, provider);
        await using var monitor = new TaskQueueBackpressureMonitor<string>(queue, new TaskQueueBackpressureMonitorOptions
        {
            HighWatermark = 3,
            LowWatermark = 1,
            Cooldown = TimeSpan.FromMilliseconds(2)
        });
        await using var diagnostics = new TaskQueueBackpressureDiagnosticsListener();
        using var subscription = monitor.RegisterListener(diagnostics);

        var producer = Task.Run(async () =>
        {
            for (var i = 0; i < 6; i++)
            {
                await queue.EnqueueAsync($"job-{i}", TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken);

        var consumer = Task.Run(async () =>
        {
            provider.Advance(TimeSpan.FromMilliseconds(5));
            for (var i = 0; i < 6; i++)
            {
                var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
                await lease.CompleteAsync(TestContext.Current.CancellationToken);
                provider.Advance(TimeSpan.FromMilliseconds(5));
            }
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(producer, consumer);

        TaskQueueBackpressureSignal first;
        do
        {
            first = await diagnostics.Reader.ReadAsync(TestContext.Current.CancellationToken);
        }
        while (!first.IsActive);

        TaskQueueBackpressureSignal second;
        do
        {
            second = await diagnostics.Reader.ReadAsync(TestContext.Current.CancellationToken);
        }
        while (second.IsActive);

        first.IsActive.ShouldBeTrue();
        second.IsActive.ShouldBeFalse();
        (first.ObservedAt <= second.ObservedAt).ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitForDrainingAsync_ShouldWorkWithSafeTaskQueue()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<string>(new TaskQueueOptions { Capacity = 8 }, provider);
        await using var safeQueue = new SafeTaskQueueWrapper<string>(queue);
        await using var monitor = new TaskQueueBackpressureMonitor<string>(safeQueue, new TaskQueueBackpressureMonitorOptions
        {
            HighWatermark = 2,
            LowWatermark = 1,
            Cooldown = TimeSpan.FromMilliseconds(1)
        });

        (await safeQueue.EnqueueAsync("alpha", TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await safeQueue.EnqueueAsync("beta", TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
        (await safeQueue.EnqueueAsync("gamma", TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        var draining = monitor.WaitForDrainingAsync(TestContext.Current.CancellationToken).AsTask();

        var consumer = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                var lease = await safeQueue.LeaseAsync(TestContext.Current.CancellationToken);
                lease.IsSuccess.ShouldBeTrue();
                var safeLease = lease.Value;
                await safeLease.CompleteAsync(TestContext.Current.CancellationToken);
                provider.Advance(TimeSpan.FromMilliseconds(2));
            }
        }, TestContext.Current.CancellationToken);

        await consumer;
        var cleared = await draining;
        cleared.IsActive.ShouldBeFalse();
        (cleared.PendingCount <= 1).ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task Backpressure_ShouldClearAtDerivedLowWatermark()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Capacity = 32 }, provider);
        await using var monitor = new TaskQueueBackpressureMonitor<int>(queue, new TaskQueueBackpressureMonitorOptions
        {
            HighWatermark = 6,
            LowWatermark = -1,
            Cooldown = TimeSpan.FromMilliseconds(1)
        });

        for (var i = 0; i < 6; i++)
        {
            await queue.EnqueueAsync(i, TestContext.Current.CancellationToken);
        }

        await EventuallyAsync(() => monitor.IsActive);
        monitor.CurrentSignal.LowWatermark.ShouldBe(3);

        for (var i = 0; i < 4; i++)
        {
            var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
            await lease.CompleteAsync(TestContext.Current.CancellationToken);
            provider.Advance(TimeSpan.FromMilliseconds(2));
        }

        await EventuallyAsync(() => !monitor.IsActive);
        monitor.CurrentSignal.IsActive.ShouldBeFalse();
        monitor.CurrentSignal.PendingCount.ShouldBeLessThanOrEqualTo(3);
    }

    private static async Task EventuallyAsync(Func<bool> condition, int attempts = 25, int delayMs = 10)
    {
        for (var i = 0; i < attempts; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(delayMs, TestContext.Current.CancellationToken);
        }

        condition().ShouldBeTrue();
    }
}
