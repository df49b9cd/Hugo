using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

internal class TimerTests
{
    [Fact]
    public async Task After_WithFakeTimeProvider_ShouldEmitOnce()
    {
        var provider = new FakeTimeProvider();
        var reader = After(TimeSpan.FromSeconds(5), provider, TestContext.Current.CancellationToken);

        var readTask = reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        Assert.False(readTask.IsCompleted);

        provider.Advance(TimeSpan.FromSeconds(5));

        var value = await readTask.ConfigureAwait(false);
        Assert.Equal(provider.GetUtcNow(), value);

        await Assert.ThrowsAsync<ChannelClosedException>(() => reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()).ConfigureAwait(false);
    }

    [Fact]
    public async Task AfterAsync_WithZeroDelay_ShouldCompleteImmediately()
    {
        var provider = new FakeTimeProvider();
        var task = AfterAsync(TimeSpan.Zero, provider, TestContext.Current.CancellationToken);

        Assert.True(task.IsCompleted);
        var timestamp = await task.ConfigureAwait(false);
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

        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task.ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Fact]
    public async Task NewTicker_ShouldProduceTicksUntilStopped()
    {
        var provider = new FakeTimeProvider();
        await using var ticker = NewTicker(TimeSpan.FromSeconds(2), provider, TestContext.Current.CancellationToken).ConfigureAwait(false);

        var firstTask = ticker.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        provider.Advance(TimeSpan.FromSeconds(2));
        var first = await firstTask.ConfigureAwait(false);

        var secondTask = ticker.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        provider.Advance(TimeSpan.FromSeconds(2));
        var second = await secondTask.ConfigureAwait(false);

        Assert.NotEqual(first, second);
        Assert.Equal(TimeSpan.FromSeconds(2), second - first);

        await ticker.StopAsync().ConfigureAwait(false);

        await Assert.ThrowsAsync<ChannelClosedException>(() => ticker.ReadAsync(TestContext.Current.CancellationToken).AsTask()).ConfigureAwait(false);
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
        DateTimeOffset first = await firstTask.ConfigureAwait(false);

        var secondTask = reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        provider.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset second = await secondTask.ConfigureAwait(false);

        Assert.Equal(TimeSpan.FromSeconds(1), second - first);

        await cts.CancelAsync().ConfigureAwait(false);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()).ConfigureAwait(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await reader.Completion.ConfigureAwait(false)).ConfigureAwait(false);
    }

    [Fact]
    public async Task GoTicker_TryRead_ShouldReflectAvailability()
    {
        var provider = new FakeTimeProvider();
        await using var ticker = NewTicker(TimeSpan.FromSeconds(1), provider, TestContext.Current.CancellationToken).ConfigureAwait(false);

        Assert.False(ticker.TryRead(out _));

        var firstTask = ticker.ReadAsync(TestContext.Current.CancellationToken).AsTask();
        provider.Advance(TimeSpan.FromSeconds(1));
        DateTimeOffset first = await firstTask.ConfigureAwait(false);

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

        await ticker.StopAsync().ConfigureAwait(false);
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
