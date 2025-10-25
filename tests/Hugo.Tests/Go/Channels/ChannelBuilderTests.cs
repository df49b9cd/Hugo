using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;

using static Hugo.Go;

namespace Hugo.Tests;

public sealed class ChannelBuilderTests
{
    [Fact]
    public void BoundedChannelBuilder_ShouldApplyDropOldest()
    {
        var channel = BoundedChannel<int>(capacity: 1)
            .WithFullMode(BoundedChannelFullMode.DropOldest)
            .Build();

        Assert.True(channel.Writer.TryWrite(1));
        Assert.True(channel.Writer.TryWrite(2));
        Assert.True(channel.Reader.TryRead(out var value));
        Assert.Equal(2, value);
    }

    [Fact]
    public void BoundedChannelBuilder_WaitFullMode_ShouldRejectExtraWrites()
    {
        var channel = BoundedChannel<int>(capacity: 1).Build();

        Assert.True(channel.Writer.TryWrite(1));
        Assert.False(channel.Writer.TryWrite(2));
    }

    [Fact]
    public void AddBoundedChannel_ShouldRegisterDependencies()
    {
        var services = new ServiceCollection();
        services.AddBoundedChannel<int>(capacity: 8, static builder => builder.SingleReader().SingleWriter());

        using var provider = services.BuildServiceProvider();

        var channel = provider.GetRequiredService<Channel<int>>();
        var reader = provider.GetRequiredService<ChannelReader<int>>();
        var writer = provider.GetRequiredService<ChannelWriter<int>>();

        Assert.Same(channel.Reader, reader);
        Assert.Same(channel.Writer, writer);
    }

    [Fact]
    public void AddBoundedChannel_ShouldRespectLifetime()
    {
        var services = new ServiceCollection();
        services.AddBoundedChannel<int>(capacity: 1, lifetime: ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        var channelScope1 = scope1.ServiceProvider.GetRequiredService<Channel<int>>();
        var channelScope1Again = scope1.ServiceProvider.GetRequiredService<Channel<int>>();
        var channelScope2 = scope2.ServiceProvider.GetRequiredService<Channel<int>>();

        Assert.Same(channelScope1, channelScope1Again);
        Assert.NotSame(channelScope1, channelScope2);
    }

    [Fact]
    public async Task PrioritizedChannelBuilder_ShouldRespectPriorityOrdering()
    {
        var channel = PrioritizedChannel<int>()
            .WithPriorityLevels(3)
            .Build();

        await channel.PrioritizedWriter.WriteAsync(1, priority: 2, TestContext.Current.CancellationToken);
        await channel.PrioritizedWriter.WriteAsync(2, priority: 0, TestContext.Current.CancellationToken);
        await channel.PrioritizedWriter.WriteAsync(3, priority: 1, TestContext.Current.CancellationToken);
        channel.PrioritizedWriter.TryComplete();

        var first = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var second = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
        var third = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, first);
        Assert.Equal(3, second);
        Assert.Equal(1, third);
    }

    [Fact]
    public void AddPrioritizedChannel_ShouldRegisterComponents()
    {
        var services = new ServiceCollection();
        services.AddPrioritizedChannel<int>(priorityLevels: 2, static builder => builder.WithDefaultPriority(0));

        using var provider = services.BuildServiceProvider();

        var channel = provider.GetRequiredService<PrioritizedChannel<int>>();
        var reader = provider.GetRequiredService<ChannelReader<int>>();
        var writer = provider.GetRequiredService<ChannelWriter<int>>();
        var prioritizedReader = provider.GetRequiredService<PrioritizedChannel<int>.PrioritizedChannelReader>();
        var prioritizedWriter = provider.GetRequiredService<PrioritizedChannel<int>.PrioritizedChannelWriter>();

        Assert.Same(channel.Reader, reader);
        Assert.Same(channel.Writer, writer);
        Assert.Same(channel.PrioritizedReader, prioritizedReader);
        Assert.Same(channel.PrioritizedWriter, prioritizedWriter);
    }

    [Fact]
    public void PrioritizedChannelBuilder_WithInvalidPriorityLevels_ShouldThrow() => Assert.Throws<ArgumentOutOfRangeException>(static () => PrioritizedChannel<int>(priorityLevels: 0));

    [Fact]
    public void PrioritizedChannelBuilder_WithInvalidDefaultPriority_ShouldThrow()
    {
        var builder = PrioritizedChannel<int>(priorityLevels: 2);

        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithDefaultPriority(2));
    }

    [Fact]
    public void AddBoundedChannel_WithNullServices_ShouldThrow() => Assert.Throws<ArgumentNullException>(static () => ChannelServiceCollectionExtensions.AddBoundedChannel<int>(null!, 1));

    [Fact]
    public void AddPrioritizedChannel_WithNullServices_ShouldThrow() => Assert.Throws<ArgumentNullException>(static () => ChannelServiceCollectionExtensions.AddPrioritizedChannel<int>(null!));
}
