using System.Threading.Channels;
using Shouldly;

using Microsoft.Extensions.DependencyInjection;

using static Hugo.Go;

namespace Hugo.Tests;

public sealed class ChannelBuilderTests
{
    [Fact(Timeout = 15_000)]
    public void BoundedChannelBuilder_ShouldApplyDropOldest()
    {
        var channel = BoundedChannel<int>(capacity: 1)
            .WithFullMode(BoundedChannelFullMode.DropOldest)
            .Build();

        channel.Writer.TryWrite(1).ShouldBeTrue();
        channel.Writer.TryWrite(2).ShouldBeTrue();
        channel.Reader.TryRead(out var value).ShouldBeTrue();
        value.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public void BoundedChannelBuilder_WaitFullMode_ShouldRejectExtraWrites()
    {
        var channel = BoundedChannel<int>(capacity: 1).Build();

        channel.Writer.TryWrite(1).ShouldBeTrue();
        channel.Writer.TryWrite(2).ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public void AddBoundedChannel_ShouldRegisterDependencies()
    {
        var services = new ServiceCollection();
        services.AddBoundedChannel<int>(capacity: 8, static builder => builder.SingleReader().SingleWriter());

        using var provider = services.BuildServiceProvider();

        var channel = provider.GetRequiredService<Channel<int>>();
        var reader = provider.GetRequiredService<ChannelReader<int>>();
        var writer = provider.GetRequiredService<ChannelWriter<int>>();

        reader.ShouldBeSameAs(channel.Reader);
        writer.ShouldBeSameAs(channel.Writer);
    }

    [Fact(Timeout = 15_000)]
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

        channelScope1Again.ShouldBeSameAs(channelScope1);
        channelScope2.ShouldNotBeSameAs(channelScope1);
    }

    [Fact(Timeout = 15_000)]
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

        first.ShouldBe(2);
        second.ShouldBe(3);
        third.ShouldBe(1);
    }

    [Fact(Timeout = 15_000)]
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

        reader.ShouldBeSameAs(channel.Reader);
        writer.ShouldBeSameAs(channel.Writer);
        prioritizedReader.ShouldBeSameAs(channel.PrioritizedReader);
        prioritizedWriter.ShouldBeSameAs(channel.PrioritizedWriter);
    }

    [Fact(Timeout = 15_000)]
    public void PrioritizedChannelBuilder_WithInvalidPriorityLevels_ShouldThrow() => Should.Throw<ArgumentOutOfRangeException>(static () => PrioritizedChannel<int>(priorityLevels: 0));

    [Fact(Timeout = 15_000)]
    public void PrioritizedChannelBuilder_WithInvalidDefaultPriority_ShouldThrow()
    {
        var builder = PrioritizedChannel<int>(priorityLevels: 2);

        Should.Throw<ArgumentOutOfRangeException>(() => builder.WithDefaultPriority(2));
    }

    [Fact(Timeout = 15_000)]
    public void AddBoundedChannel_WithNullServices_ShouldThrow() => Should.Throw<ArgumentNullException>(static () => ChannelServiceCollectionExtensions.AddBoundedChannel<int>(null!, 1));

    [Fact(Timeout = 15_000)]
    public void AddPrioritizedChannel_WithNullServices_ShouldThrow() => Should.Throw<ArgumentNullException>(static () => ChannelServiceCollectionExtensions.AddPrioritizedChannel<int>(null!));
}
