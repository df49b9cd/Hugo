using System.Threading.Channels;

using Shouldly;

using static Hugo.Go;

namespace Hugo.Tests;

public class GoSharpTests
{
    [Fact(Timeout = 15_000)]
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
            }, cancellationToken: TestContext.Current.CancellationToken);
        }

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        counter.ShouldBe(3);
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitGroup_Go_WithFaultedTask_ShouldStillComplete()
    {
        var wg = new WaitGroup();

        wg.Go(static async () =>
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
            throw new InvalidOperationException("boom");
        }, cancellationToken: TestContext.Current.CancellationToken);

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        true.ShouldBeTrue();
    }

    private static readonly int[] expected = [0, 2, 1];

    [Fact(Timeout = 15_000)]
    public void Defer_ShouldExecuteInReverseOrder()
    {
        var order = new List<int>();

        using (Defer(() => order.Add(1)))
        using (Defer(() => order.Add(2)))
        {
            order.Add(0);
        }

        order.ShouldBe(expected);
    }

    [Fact(Timeout = 15_000)]
    public void Defer_ShouldThrow_WhenActionNull() =>
        Should.Throw<ArgumentNullException>(static () => new Defer(null!));

    [Fact(Timeout = 15_000)]
    public void Err_WithExceptionAndCode_ShouldCaptureMetadata()
    {
        var ex = new InvalidOperationException("boom");
        var result = Err<int>(ex, "custom.code");

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe("custom.code");
        result.Error?.Metadata.ContainsKey("exceptionType").ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task MakeChannel_BoundedOptions_ShouldDropOldestWhenFull()
    {
        var options = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true
        };

        var channel = MakeChannel<int>(options);

        channel.Writer.TryWrite(1).ShouldBeTrue();
        channel.Writer.TryWrite(2).ShouldBeTrue();

        var value = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);

        value.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async Task MakeChannel_UnboundedOptions_ShouldAllowMultipleWrites()
    {
        var options = new UnboundedChannelOptions { SingleReader = true, SingleWriter = true };
        var channel = MakeChannel<int>(options);

        for (var i = 0; i < 5; i++)
        {
            channel.Writer.TryWrite(i).ShouldBeTrue();
        }

        for (var i = 0; i < 5; i++)
        {
            var value = await channel.Reader.ReadAsync(TestContext.Current.CancellationToken);
            value.ShouldBe(i);
        }
    }
}
