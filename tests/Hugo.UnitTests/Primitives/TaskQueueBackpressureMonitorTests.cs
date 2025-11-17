using System.Threading.RateLimiting;
using Shouldly;

using Hugo.TaskQueues.Backpressure;

using Microsoft.Extensions.Time.Testing;

namespace Hugo.Tests.Primitives;

public class TaskQueueBackpressureMonitorTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask Monitor_ShouldEmitSignals_WhenThresholdsCrossed()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<string>(new TaskQueueOptions { Capacity = 16 }, provider);
        await using var monitor = new TaskQueueBackpressureMonitor<string>(
            queue,
            new TaskQueueBackpressureMonitorOptions
            {
                HighWatermark = 2,
                LowWatermark = 1,
                Cooldown = TimeSpan.FromMilliseconds(5)
            });
        await using var diagnostics = new TaskQueueBackpressureDiagnosticsListener();
        using var subscription = monitor.RegisterListener(diagnostics);

        await queue.EnqueueAsync("alpha", TestContext.Current.CancellationToken);
        await queue.EnqueueAsync("beta", TestContext.Current.CancellationToken);
        await queue.EnqueueAsync("gamma", TestContext.Current.CancellationToken);

        TaskQueueBackpressureSignal activated;
        do
        {
            activated = await diagnostics.Reader.ReadAsync(TestContext.Current.CancellationToken);
        }
        while (!activated.IsActive);
        activated.IsActive.ShouldBeTrue();

        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await lease.CompleteAsync(TestContext.Current.CancellationToken);
        provider.Advance(TimeSpan.FromMilliseconds(6));
        var lease2 = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await lease2.CompleteAsync(TestContext.Current.CancellationToken);

        TaskQueueBackpressureSignal cleared;
        do
        {
            cleared = await diagnostics.Reader.ReadAsync(TestContext.Current.CancellationToken);
        }
        while (cleared.IsActive);
        cleared.IsActive.ShouldBeFalse();
        queue.PendingCount.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask WaitForDrainingAsync_ShouldCompleteOrCancel()
    {
        var provider = new FakeTimeProvider();
        await using var queue = new TaskQueue<int>(new TaskQueueOptions { Capacity = 8 }, provider);
        await using var monitor = new TaskQueueBackpressureMonitor<int>(
            queue,
            new TaskQueueBackpressureMonitorOptions
            {
                HighWatermark = 2,
                LowWatermark = 1,
                Cooldown = TimeSpan.FromMilliseconds(2)
            });
        await using var diagnostics = new TaskQueueBackpressureDiagnosticsListener();
        using var subscription = monitor.RegisterListener(diagnostics);

        await queue.EnqueueAsync(1, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(2, TestContext.Current.CancellationToken);
        await queue.EnqueueAsync(3, TestContext.Current.CancellationToken);
        TaskQueueBackpressureSignal activation;
        do
        {
            activation = await diagnostics.Reader.ReadAsync(TestContext.Current.CancellationToken);
        }
        while (!activation.IsActive);

        var drainingTask = monitor.WaitForDrainingAsync(TestContext.Current.CancellationToken).AsTask();

        using var cancelCts = new CancellationTokenSource();
        var canceled = monitor.WaitForDrainingAsync(cancelCts.Token).AsTask();
        await cancelCts.CancelAsync();
        await Should.ThrowAsync<OperationCanceledException>(async () => await canceled);

        var lease = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await lease.CompleteAsync(TestContext.Current.CancellationToken);
        provider.Advance(TimeSpan.FromMilliseconds(4));
        var lease2 = await queue.LeaseAsync(TestContext.Current.CancellationToken);
        await lease2.CompleteAsync(TestContext.Current.CancellationToken);

        var cleared = await drainingTask;
        cleared.IsActive.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public void MonitorOptions_ShouldValidateBoundaries()
    {
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new TaskQueueBackpressureMonitorOptions
            {
                HighWatermark = 0
            });

        Should.Throw<ArgumentOutOfRangeException>(() =>
            new TaskQueueBackpressureMonitorOptions
            {
                HighWatermark = 2,
                Cooldown = TimeSpan.Zero
            });
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask Monitor_ShouldDeriveLowWatermark_WhenUnsetOrAboveHigh()
    {
        var provider = new FakeTimeProvider();
        await using var firstQueue = new TaskQueue<int>(new TaskQueueOptions { Capacity = 8 }, provider);
        await using var monitor = new TaskQueueBackpressureMonitor<int>(
            firstQueue,
            new TaskQueueBackpressureMonitorOptions
            {
                HighWatermark = 6,
                LowWatermark = -1,
                Cooldown = TimeSpan.FromMilliseconds(2)
            });

        monitor.CurrentSignal.LowWatermark.ShouldBe(3);

        await using var secondQueue = new TaskQueue<int>(new TaskQueueOptions { Capacity = 8 }, provider);
        await using var derivedMonitor = new TaskQueueBackpressureMonitor<int>(
            secondQueue,
            new TaskQueueBackpressureMonitorOptions
            {
                HighWatermark = 6,
                LowWatermark = 12,
                Cooldown = TimeSpan.FromMilliseconds(2)
            });

        derivedMonitor.CurrentSignal.LowWatermark.ShouldBe(3);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask DiagnosticsListener_ShouldBoundHistory()
    {
        await using var diagnostics = new TaskQueueBackpressureDiagnosticsListener(capacity: 2);

        for (var i = 0; i < 5; i++)
        {
            var signal = new TaskQueueBackpressureSignal(
                IsActive: i % 2 == 0,
                PendingCount: i,
                HighWatermark: 4,
                LowWatermark: 2,
                ObservedAt: DateTimeOffset.UtcNow.AddSeconds(i));

            await diagnostics.OnSignalAsync(signal, TestContext.Current.CancellationToken);
        }

        var retained = new List<long>();
        while (diagnostics.Reader.TryRead(out TaskQueueBackpressureSignal? signal) && signal is not null)
        {
            retained.Add(signal.PendingCount);
        }

        retained.ShouldBe(new[] { 3L, 4L });
        diagnostics.Latest.PendingCount.ShouldBe(4);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask BackpressureAwareRateLimiter_ShouldSwapLimiters()
    {
        using var baseline = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 5,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
        using var throttled = new ConcurrencyLimiter(new ConcurrencyLimiterOptions
        {
            PermitLimit = 1,
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst
        });
        using var listener = new BackpressureAwareRateLimiter(baseline, throttled);

        listener.CurrentLimiter.ShouldBeSameAs(baseline);
        listener.LimiterSelector().ShouldBeSameAs(baseline);

        await listener.OnSignalAsync(new TaskQueueBackpressureSignal(true, 32, 16, 8, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        listener.CurrentLimiter.ShouldBeSameAs(throttled);

        await listener.OnSignalAsync(new TaskQueueBackpressureSignal(false, 2, 16, 8, DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        listener.LimiterSelector().ShouldBeSameAs(baseline);
    }
}
