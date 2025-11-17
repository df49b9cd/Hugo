using System.Threading.Channels;
using Shouldly;

using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

public class TimerTests
{
    [Fact(Timeout = 15_000)]
    public async Task After_WithFakeTimeProvider_ShouldEmitOnce()
    {
        var provider = new FakeTimeProvider();
        var reader = After(TimeSpan.FromSeconds(5), provider, TestContext.Current.CancellationToken);

        var readTask = reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        readTask.IsCompleted.ShouldBeFalse();

        provider.Advance(TimeSpan.FromSeconds(5));

        var value = await readTask;
        value.ShouldBe(provider.GetUtcNow());

        await Should.ThrowAsync<ChannelClosedException>(() => reader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact(Timeout = 15_000)]
    public async Task AfterAsync_WithZeroDelay_ShouldCompleteImmediately()
    {
        var provider = new FakeTimeProvider();
        var task = AfterAsync(TimeSpan.Zero, provider, TestContext.Current.CancellationToken);

        task.IsCompleted.ShouldBeTrue();
        var timestamp = await task;
        timestamp.ShouldBe(provider.GetUtcNow());
    }

    [Fact(Timeout = 15_000)]
    public async Task AfterValueTaskAsync_WithZeroDelay_ShouldCompleteImmediately()
    {
        var provider = new FakeTimeProvider();
        ValueTask<DateTimeOffset> task = AfterValueTaskAsync(TimeSpan.Zero, provider, TestContext.Current.CancellationToken);

        task.IsCompleted.ShouldBeTrue();
        var timestamp = await task;
        timestamp.ShouldBe(provider.GetUtcNow());
    }

    [Fact(Timeout = 15_000)]
    public void After_ShouldThrow_WhenDelayIsInfinite() =>
        Should.Throw<ArgumentOutOfRangeException>(static () => After(Timeout.InfiniteTimeSpan, provider: TimeProvider.System, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public void After_ShouldThrow_WhenDelayIsNegative() =>
        Should.Throw<ArgumentOutOfRangeException>(static () => After(TimeSpan.FromMilliseconds(-1), provider: TimeProvider.System, cancellationToken: TestContext.Current.CancellationToken));

    [Fact(Timeout = 15_000)]
    public async Task AfterAsync_ShouldRespectCancellation()
    {
        var provider = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        ValueTask<DateTimeOffset> task = AfterAsync(TimeSpan.FromSeconds(5), provider, cts.Token);

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await task.AsTask());
    }

    [Fact(Timeout = 15_000)]
    public async Task AfterValueTaskAsync_ShouldRespectCancellation()
    {
        var provider = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        ValueTask<DateTimeOffset> task = AfterValueTaskAsync(TimeSpan.FromSeconds(5), provider, cts.Token);

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () => await task.AsTask());
    }

    [Fact(Timeout = 15_000)]
    public async Task NewTicker_ShouldProduceTicksUntilStopped()
    {
        var provider = new FakeTimeProvider();
        await using var ticker = NewTicker(TimeSpan.FromSeconds(2), provider, TestContext.Current.CancellationToken);

        var firstTask = ticker.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        provider.Advance(TimeSpan.FromSeconds(2));
        var first = await firstTask;

        var secondTask = ticker.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        provider.Advance(TimeSpan.FromSeconds(2));
        var second = await secondTask;

        second.ShouldNotBe(first);
        second - first.ShouldBe(TimeSpan.FromSeconds(2));

        await ticker.StopAsync();

        await Should.ThrowAsync<ChannelClosedException>(() => ticker.ReadAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NewTicker_ShouldThrow_WhenPeriodNonPositive(double seconds)
    {
        TimeSpan period = TimeSpan.FromSeconds(seconds);

        Should.Throw<ArgumentOutOfRangeException>(() => NewTicker(period, provider: TimeProvider.System, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact(Timeout = 15_000)]
    public async Task Tick_ShouldProduceTicks_AndRespectCancellation()
    {
        var provider = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        ChannelReader<DateTimeOffset> reader = Tick(TimeSpan.FromSeconds(1), provider, cts.Token);

        var firstTask = reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        provider.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset first = await firstTask;

        var secondTask = reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        provider.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset second = await secondTask;

        second - first.ShouldBe(TimeSpan.FromSeconds(1));

        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => reader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        await Should.ThrowAsync<OperationCanceledException>(async () => await reader.Completion);
    }

    [Fact(Timeout = 15_000)]
    public async Task GoTicker_TryRead_ShouldReflectAvailability()
    {
        var provider = new FakeTimeProvider();
        await using var ticker = NewTicker(TimeSpan.FromSeconds(1), provider, TestContext.Current.CancellationToken);

        ticker.TryRead(out _).ShouldBeFalse();

        var firstTask = ticker.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        provider.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset first = await firstTask;

        provider.Advance(TimeSpan.FromSeconds(1));

        DateTimeOffset second = default;
        var available = false;
        for (var attempt = 0; attempt < 5 && !available; attempt++)
        {
            available = ticker.TryRead(out second);
            if (!available)
            {
                await Task.Yield();
            }
        }

        available.ShouldBeTrue();
        second - first.ShouldBe(TimeSpan.FromSeconds(1));

        await ticker.StopAsync();
    }

    [Fact(Timeout = 15_000)]
    public void GoTicker_Stop_ShouldCompleteReader()
    {
        var provider = new FakeTimeProvider();
        var ticker = NewTicker(TimeSpan.FromSeconds(1), provider, TestContext.Current.CancellationToken);

        ticker.Stop();

        ticker.Reader.Completion.IsCompleted.ShouldBeTrue();

        ticker.Dispose();
    }
}
