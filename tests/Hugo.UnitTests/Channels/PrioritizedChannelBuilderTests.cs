using System.Threading.Channels;


namespace Hugo.Tests.Channels;

public sealed class PrioritizedChannelBuilderTests
{
    [Fact(Timeout = 15_000)]
    public void Build_ShouldApplyCustomOptions()
    {
        var channel = Go.PrioritizedChannel<int>(priorityLevels: 4)
            .WithCapacityPerLevel(8)
            .WithDefaultPriority(2)
            .WithPrefetchPerPriority(3)
            .WithFullMode(BoundedChannelFullMode.DropOldest)
            .SingleReader()
            .SingleWriter(false)
            .Build();

        var writer = channel.PrioritizedWriter;
        var reader = channel.Reader;

        writer.TryWrite(5, priority: 2).ShouldBeTrue();
        reader.TryRead(out var value).ShouldBeTrue();
        value.ShouldBe(5);
    }
}
