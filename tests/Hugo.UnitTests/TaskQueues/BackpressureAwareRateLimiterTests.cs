using System.Threading.RateLimiting;

using Hugo.TaskQueues.Backpressure;

using Shouldly;

namespace Hugo.Tests;

[Collection("TaskQueueConcurrency")]
public class BackpressureAwareRateLimiterTests
{
    [Fact(Timeout = 15_000)]
    public async Task OnSignalAsync_ShouldSwitchLimiterAndDisposeTrackedInstances()
    {
        var unthrottled = new TrackingLimiter();
        var throttled = new TrackingLimiter();

        using var listener = new BackpressureAwareRateLimiter(
            unthrottled,
            throttled,
            disposeUnthrottledLimiter: true,
            disposeBackpressureLimiter: true);

        listener.CurrentLimiter.ShouldBe(unthrottled);

        await listener.OnSignalAsync(new TaskQueueBackpressureSignal(
            IsActive: true,
            PendingCount: 5,
            HighWatermark: 10,
            LowWatermark: 5,
            ObservedAt: DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        listener.CurrentLimiter.ShouldBe(throttled);

        await listener.OnSignalAsync(new TaskQueueBackpressureSignal(
            IsActive: false,
            PendingCount: 0,
            HighWatermark: 10,
            LowWatermark: 5,
            ObservedAt: DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
        listener.CurrentLimiter.ShouldBe(unthrottled);

        listener.Dispose();

        unthrottled.Disposed.ShouldBeTrue();
        throttled.Disposed.ShouldBeTrue();
    }

    private sealed class TrackingLimiter : RateLimiter
    {
        public bool Disposed { get; private set; }

        public override TimeSpan? IdleDuration => null;

        protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken) =>
            new(new NoopLease());

        protected override RateLimitLease AttemptAcquireCore(int permitCount) => new NoopLease();

        public override RateLimiterStatistics? GetStatistics() => null;

        protected override void Dispose(bool disposing) => Disposed = disposing;

        private sealed class NoopLease : RateLimitLease
        {
            public override bool IsAcquired => true;
            public override IEnumerable<string> MetadataNames => Array.Empty<string>();
            public override bool TryGetMetadata(string name, out object? value)
            {
                value = null;
                return false;
            }
        }
    }
}
