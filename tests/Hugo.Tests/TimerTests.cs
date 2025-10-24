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
}
