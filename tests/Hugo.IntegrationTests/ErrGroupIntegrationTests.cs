using Hugo.Policies;

using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public class ErrGroupIntegrationTests
{
    [Fact]
    public async Task TieredFallbackAsync_ShouldSurfaceErrGroupDisposalAsFailure()
    {
        var tiers = new[]
        {
            new ResultFallbackTier<int>(
                "misuse",
                new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>[]
                {
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
                })
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
        Assert.Contains(nameof(ErrGroup), result.Error?.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TieredFallbackAsync_ShouldSucceedWhenPrimaryCancelsPeers()
    {
        var tiers = new[]
        {
            new ResultFallbackTier<int>(
                "primary",
                new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>[]
                {
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
                })
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
    }

    [Fact]
    public async Task PipelineGo_ShouldCancelPeersBeforeCompensationCompletes()
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
        Assert.Same(cancellationObserved.Task, completed);
        Assert.False(releaseCompensation.Task.IsCompleted);

        releaseCompensation.TrySetResult();

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
    }
}
