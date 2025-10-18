using System.Threading.Channels;
using static Hugo.Go;

namespace Hugo.Tests;

public class GoSharpTests
{
    [Fact]
    public async Task WaitGroup_Go_WithCancellationAwareWork_ShouldComplete()
    {
        var wg = new WaitGroup();
        var counter = 0;

        for (var i = 0; i < 3; i++)
        {
            wg.Go(async token =>
            {
                await Task.Delay(10, token);
                Interlocked.Increment(ref counter);
            }, TestContext.Current.CancellationToken);
        }

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, counter);
    }

    [Fact]
    public async Task WaitGroup_Go_WithFaultedTask_ShouldStillComplete()
    {
        var wg = new WaitGroup();

        wg.Go(async () =>
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
            throw new InvalidOperationException("boom");
        }, TestContext.Current.CancellationToken);

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(true);
    }

    [Fact]
    public void Defer_ShouldExecuteInReverseOrder()
    {
        var order = new List<int>();

        using (Defer(() => order.Add(1)))
        using (Defer(() => order.Add(2)))
        {
            order.Add(0);
        }

        Assert.Equal(new[] { 0, 2, 1 }, order);
    }

    [Fact]
    public void Err_WithExceptionAndCode_ShouldCaptureMetadata()
    {
        var ex = new InvalidOperationException("boom");
        var result = Err<int>(ex, "custom.code");

        Assert.True(result.IsFailure);
        Assert.Equal("custom.code", result.Error?.Code);
        Assert.True(result.Error?.Metadata.ContainsKey("exceptionType"));
    }

    [Fact]
    public async Task MakeChannel_BoundedOptions_ShouldDropOldestWhenFull()
    {
        var options = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        };

        var channel = MakeChannel<int>(options);

        Assert.True(channel.Writer.TryWrite(1));
        Assert.True(channel.Writer.TryWrite(2));

        var value = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, value);
    }

    [Fact]
    public async Task MakeChannel_UnboundedOptions_ShouldAllowMultipleWrites()
    {
        var options = new UnboundedChannelOptions { SingleReader = true, SingleWriter = true };
        var channel = MakeChannel<int>(options);

        for (var i = 0; i < 5; i++)
        {
            Assert.True(channel.Writer.TryWrite(i));
        }

        for (var i = 0; i < 5; i++)
        {
            var value = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
            Assert.Equal(i, value);
        }
    }
}
