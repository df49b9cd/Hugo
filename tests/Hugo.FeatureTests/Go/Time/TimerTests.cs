using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

public class TimerTests
{
    [Fact]
    public async Task After_WithFakeTimeProvider_ShouldEmitOnce()
    {
        var provider = new FakeTimeProvider();
        var reader = After(TimeSpan.FromSeconds(5), provider, TestContext.Current.CancellationToken);

        var readTask = reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(readTask.IsCompleted);

        provider.Advance(TimeSpan.FromSeconds(5));

        var value = await readTask;
        Assert.Equal(provider.GetUtcNow(), value);

        await Assert.ThrowsAsync<ChannelClosedException>(() => reader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task AfterAsync_WithZeroDelay_ShouldCompleteImmediately()
    {
        var provider = new FakeTimeProvider();
        var task = AfterAsync(TimeSpan.Zero, provider, TestContext.Current.CancellationToken);

        Assert.True(task.IsCompleted);
        var timestamp = await task;
        Assert.Equal(provider.GetUtcNow(), timestamp);
    }

    [Fact]
    public async Task AfterValueTaskAsync_WithZeroDelay_ShouldCompleteImmediately()
    {
        var provider = new FakeTimeProvider();
        ValueTask<DateTimeOffset> task = AfterValueTaskAsync(TimeSpan.Zero, provider, TestContext.Current.CancellationToken);

        Assert.True(task.IsCompleted);
        var timestamp = await task;
        Assert.Equal(provider.GetUtcNow(), timestamp);
    }

    [Fact]
    public void After_ShouldThrow_WhenDelayIsInfinite() =>
        Assert.Throws<ArgumentOutOfRangeException>(static () => After(Timeout.InfiniteTimeSpan, provider: TimeProvider.System, cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public void After_ShouldThrow_WhenDelayIsNegative() =>
        Assert.Throws<ArgumentOutOfRangeException>(static () => After(TimeSpan.FromMilliseconds(-1), provider: TimeProvider.System, cancellationToken: TestContext.Current.CancellationToken));

    [Fact]
    public async Task AfterAsync_ShouldRespectCancellation()
    {
        var provider = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        Task<DateTimeOffset> task = AfterAsync(TimeSpan.FromSeconds(5), provider, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task AfterValueTaskAsync_ShouldRespectCancellation()
    {
        var provider = new FakeTimeProvider();
        using var cts = new CancellationTokenSource();

        ValueTask<DateTimeOffset> task = AfterValueTaskAsync(TimeSpan.FromSeconds(5), provider, cts.Token);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task.AsTask());
    }

    [Fact]
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

        Assert.NotEqual(first, second);
        Assert.Equal(TimeSpan.FromSeconds(2), second - first);

        await ticker.StopAsync();

        await Assert.ThrowsAsync<ChannelClosedException>(() => ticker.ReadAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NewTicker_ShouldThrow_WhenPeriodNonPositive(double seconds)
    {
        TimeSpan period = TimeSpan.FromSeconds(seconds);

        Assert.Throws<ArgumentOutOfRangeException>(() => NewTicker(period, provider: TimeProvider.System, cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
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

        Assert.Equal(TimeSpan.FromSeconds(1), second - first);

        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(TestContext.Current.CancellationToken).AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.Completion);
    }

    [Fact]
    public async Task GoTicker_TryRead_ShouldReflectAvailability()
    {
        var provider = new FakeTimeProvider();
        await using var ticker = NewTicker(TimeSpan.FromSeconds(1), provider, TestContext.Current.CancellationToken);

        Assert.False(ticker.TryRead(out _));

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

        Assert.True(available);
        Assert.Equal(TimeSpan.FromSeconds(1), second - first);

        await ticker.StopAsync();
    }

    [Fact]
    public void GoTicker_Stop_ShouldCompleteReader()
    {
        var provider = new FakeTimeProvider();
        var ticker = NewTicker(TimeSpan.FromSeconds(1), provider, TestContext.Current.CancellationToken);

        ticker.Stop();

        Assert.True(ticker.Reader.Completion.IsCompleted);

        ticker.Dispose();
    }
}
