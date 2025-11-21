using Hugo.Policies;


using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public class ErrGroupIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask TieredFallbackAsync_ShouldSurfaceErrGroupDisposalAsFailure()
    {
        var tiers = new[]
        {
            new ResultFallbackTier<int>(
                "misuse",
                [
                    async (_, ct) =>
                    {
                        var inner = new ErrGroup(ct);
                        try
                        {
                            inner.Dispose();
                            inner.Go(static () => Task.CompletedTask);
                            await Task.Yield();
                            return Result.Ok(0);
                        }
                        finally
                        {
                            inner.Dispose();
                        }
                    }
                ])
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
        result.Error?.Message.ShouldContain(nameof(ErrGroup));
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TieredFallbackAsync_ShouldSucceedWhenPrimaryCancelsPeers()
    {
        var tiers = new[]
        {
            new ResultFallbackTier<int>(
                "primary",
                [
                    async (_, ct) =>
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(10), ct);
                        return Result.Ok(7);
                    },
                    async (_, ct) =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(30), ct);
                        }
                        catch (OperationCanceledException)
                        {
                            return Result.Fail<int>(Error.Canceled(token: ct));
                        }

                        return Result.Ok(0);
                    }
                ])
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(7);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask PipelineGo_ShouldCancelPeersBeforeCompensationCompletes()
    {
        using var group = new ErrGroup();
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var compensationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompensation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        group.Go(
            (ctx, ct) =>
            {
                Task.Run(async () =>
                {
                    compensationStarted.TrySetResult();
                    await releaseCompensation.Task;
                });

                return ValueTask.FromResult(Result.Fail<Unit>(Error.From("pipeline failed", ErrorCodes.Exception)));
            },
            stepName: "integration-step",
            policy: ResultExecutionPolicy.None,
            timeProvider: TimeProvider.System);

        group.Go(async ct =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult(true);
            }
        });

        await compensationStarted.Task;
        var completed = await Task.WhenAny(cancellationObserved.Task, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        _ = completed.ShouldBeSameAs(cancellationObserved.Task);
        releaseCompensation.Task.IsCompleted.ShouldBeFalse();

        releaseCompensation.TrySetResult();

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
    }
}
