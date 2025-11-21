using System.Threading.Channels;

using Microsoft.Extensions.Time.Testing;


using static Hugo.Go;

namespace Hugo.Tests;

public class GoFunctionalTests
{
    [Fact(Timeout = 15_000)]
    public void Err_ShouldReturnDefaultError_WhenGivenNull()
    {
        var result = Err<string>((Error?)null);
        result.IsFailure.ShouldBeTrue();
        result.Error?.Message.ShouldBe("An unspecified error occurred.");
    }

    [Fact(Timeout = 15_000)]
    public void Ok_ShouldWrapValue()
    {
        var result = Ok(42);
        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact(Timeout = 15_000)]
    public void Err_ShouldWrapExceptionWithMetadata()
    {
        var ex = new InvalidOperationException("boom");
        var result = Err<string>(ex);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
        result.Error?.Message.ShouldBe("boom");
        result.Error?.Metadata.ContainsKey("exceptionType").ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task Run_WithCancellationToken_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource(50);

        var task = Run(async ct =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            },
        cancellationToken: cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => task.AsTask());
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithBoundedOptions_ShouldRespectSettings()
    {
        var channel = MakeChannel<int>(capacity: 1, fullMode: BoundedChannelFullMode.DropOldest, singleReader: true, singleWriter: true);

        channel.ShouldNotBeNull();
        channel.Reader.Completion.IsCompleted.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public void MakeChannel_WithCustomUnboundedOptions_ShouldNotThrow()
    {
        var options = new UnboundedChannelOptions { SingleReader = true, SingleWriter = false };
        var channel = MakeChannel<int>(options);

        channel.ShouldNotBeNull();
    }

    [Fact(Timeout = 15_000)]
    public async Task WaitGroup_Go_WithCancellationToken_ShouldStopEarly()
    {
        var wg = new WaitGroup();
        using var cts = new CancellationTokenSource(50);

        wg.Go(async token =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), token);
            },
        cancellationToken: cts.Token);

        await Should.ThrowAsync<OperationCanceledException>(() => wg.WaitAsync(cts.Token));
    }

    [Fact(Timeout = 15_000)]
    public async Task Integration_Pipeline_ShouldComposeGoAndFunctionalHelpers()
    {
        var channel = MakeChannel<int>(capacity: 2);
        using var mutex = new Mutex();
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
        }, cancellationToken: TestContext.Current.CancellationToken);

        var collected = new List<int>();
        await foreach (var item in channel.Reader.ReadAllAsync(TestContext.Current.CancellationToken))
        {
            collected.Add(item);
        }

        await wg.WaitAsync(TestContext.Current.CancellationToken);

        var result = Ok(collected)
            .Map(items => items.Sum())
            .Ensure(sum => sum == 3, sum => Error.From($"Unexpected sum {sum}", ErrorCodes.Validation));

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(3);
    }

    private static readonly int[] expected = [42];

    [Fact(Timeout = 15_000)]
    public async Task Integration_WithFakeTimeProvider_ShouldDriveChannelWorkflow()
    {
        var provider = new FakeTimeProvider();
        var channel = MakeChannel<int>(capacity: 1);
        var wg = new WaitGroup();
        var collected = new List<int>();

        var delayScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); // coordinate timer scheduling with fake time

        wg.Go(async token =>
        {
            ValueTask delayTask = DelayAsync(TimeSpan.FromSeconds(3), provider, token);
            delayScheduled.TrySetResult();
            await delayTask;
            await channel.Writer.WriteAsync(42, token);
            channel.Writer.TryComplete();
        }, cancellationToken: TestContext.Current.CancellationToken);

        await delayScheduled.Task; // ensure the delay is registered before advancing fake time

        ValueTask<Result<Unit>> selectTask = SelectAsync<Unit>(
            timeout: TimeSpan.FromSeconds(5),
            provider: provider,
            cancellationToken: TestContext.Current.CancellationToken,
            cases:
            [
                ChannelCase.Create(channel.Reader, (value, _) =>
                {
                    collected.Add(value);
                    return ValueTask.FromResult(Result.Ok(Unit.Value));
                })
            ]);

        selectTask.IsCompleted.ShouldBeFalse();

        provider.Advance(TimeSpan.FromSeconds(3));

        var result = await selectTask;

        result.IsSuccess.ShouldBeTrue();
        collected.ShouldBe(expected);

        await wg.WaitAsync(TestContext.Current.CancellationToken);
    }
}
