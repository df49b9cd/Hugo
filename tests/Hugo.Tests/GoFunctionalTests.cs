using System.Collections.Generic;
using System.Linq;
using System.Threading.Channels;
using Hugo;
using static Hugo.Go;

namespace Hugo.Tests;

public class GoFunctionalTests
{
    [Fact]
    public void Err_ShouldReturnDefaultError_WhenGivenNull()
    {
        var result = Err<string>((Error?)null);
        Assert.True(result.IsFailure);
        Assert.Equal("An unspecified error occurred.", result.Error?.Message);
    }

    [Fact]
    public void Ok_ShouldWrapValue()
    {
        var result = Ok(42);
        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Err_ShouldWrapExceptionWithMetadata()
    {
        var ex = new InvalidOperationException("boom");
        var result = Err<string>(ex);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Equal("boom", result.Error?.Message);
        Assert.True(result.Error?.Metadata.ContainsKey("exceptionType"));
    }

    [Fact]
    public async Task Run_WithCancellationToken_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource(50);

        var task = Run(async ct =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), ct);
        },
        cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public void MakeChannel_WithBoundedOptions_ShouldRespectSettings()
    {
        var channel = MakeChannel<int>(capacity: 1, fullMode: BoundedChannelFullMode.DropOldest, singleReader: true, singleWriter: true);

        Assert.NotNull(channel);
        Assert.False(channel.Reader.Completion.IsCompleted);
    }

    [Fact]
    public void MakeChannel_WithCustomUnboundedOptions_ShouldNotThrow()
    {
        var options = new UnboundedChannelOptions { SingleReader = true, SingleWriter = false };
        var channel = MakeChannel<int>(options);

        Assert.NotNull(channel);
    }

    [Fact]
    public async Task WaitGroup_Go_WithCancellationToken_ShouldStopEarly()
    {
        var wg = new WaitGroup();
        using var cts = new CancellationTokenSource(50);

        wg.Go(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), token);
        },
        cts.Token);

        await Assert.ThrowsAsync<OperationCanceledException>(() => wg.WaitAsync(cts.Token));
    }

    [Fact]
    public async Task Integration_Pipeline_ShouldComposeGoAndFunctionalHelpers()
    {
    var channel = MakeChannel<int>(capacity: 2);
    var mutex = new Mutex();
        var wg = new WaitGroup();

        wg.Go(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                using (await mutex.LockAsync())
                {
                    await channel.Writer.WriteAsync(i);
                }
            }

            channel.Writer.Complete();
        });

        var collected = new List<int>();
        await foreach (var item in channel.Reader.ReadAllAsync())
        {
            collected.Add(item);
        }

        await wg.WaitAsync();

        var result = Ok(collected)
            .Map(items => items.Sum())
            .Ensure(sum => sum == 3, sum => Error.From($"Unexpected sum {sum}", ErrorCodes.Validation));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value);
    }
}
