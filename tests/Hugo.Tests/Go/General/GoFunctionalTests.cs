using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

internal class GoFunctionalTests
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
            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
        },
        cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task).ConfigureAwait(false);
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
            await Task.Delay(TimeSpan.FromSeconds(5), token).ConfigureAwait(false);
        },
        cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wg.WaitAsync(cts.Token)).ConfigureAwait(false);
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
                using (await mutex.LockAsync().ConfigureAwait(false))
                {
                    await channel.Writer.WriteAsync(i).ConfigureAwait(false);
                }
            }

            channel.Writer.Complete();
        }, TestContext.Current.CancellationToken);

        var collected = new List<int>();
        await foreach (var item in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))
        {
            collected.Add(item);
        }

        await wg.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);

        var result = Ok(collected)
            .Map(items => items.Sum())
            .Ensure(sum => sum == 3, sum => Error.From($"Unexpected sum {sum}", ErrorCodes.Validation));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, result.Value);
    }

    private static readonly int[] expected = [42];

    [Fact]
    public async Task Integration_WithFakeTimeProvider_ShouldDriveChannelWorkflow()
    {
        var provider = new FakeTimeProvider();
        var channel = MakeChannel<int>(capacity: 1);
        var wg = new WaitGroup();
        var collected = new List<int>();

        var delayScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // coordinate timer scheduling with fake time

        wg.Go(async token =>
        {
            var delayTask = DelayAsync(TimeSpan.FromSeconds(3), provider, token);
            delayScheduled.TrySetResult();
            await delayTask.ConfigureAwait(false);
            await channel.Writer.WriteAsync(42, token).ConfigureAwait(false);
            channel.Writer.TryComplete();
        }, TestContext.Current.CancellationToken);

        await delayScheduled.Task.ConfigureAwait(false); // ensure the delay is registered before advancing fake time

        var selectTask = SelectAsync(
            timeout: TimeSpan.FromSeconds(5),
            provider: provider,
            cancellationToken: TestContext.Current.CancellationToken,
            cases:
            [
                ChannelCase.Create(channel.Reader, (value, _) =>
                {
                    collected.Add(value);
                    return Task.FromResult(Result.Ok(Unit.Value));
                })
            ]);

        Assert.False(selectTask.IsCompleted);

        provider.Advance(TimeSpan.FromSeconds(3));

        var result = await selectTask.ConfigureAwait(false);

        Assert.True(result.IsSuccess);
        Assert.Equal(expected, collected);

        await wg.WaitAsync(TestContext.Current.CancellationToken).ConfigureAwait(false);
    }
}
