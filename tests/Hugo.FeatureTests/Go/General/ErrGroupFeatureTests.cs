using Hugo.Policies;

using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public class ErrGroupFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async Task ErrGroupDocSample_ShouldCompleteSuccessfully()
    {
        using var group = new ErrGroup();
        var retryPolicy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 3, delay: TimeSpan.Zero));
        var attempts = 0;

        group.Go((ctx, ct) =>
        {
            return Result.RetryWithPolicyAsync(async (_, token) =>
            {
                await Task.Yield();
                attempts++;
                if (attempts < 3)
                {
                    return Result.Fail<Unit>(Error.Timeout());
                }

                return Result.Ok(Unit.Value);
            }, retryPolicy, timeProvider: ctx.TimeProvider, cancellationToken: ct);
        }, stepName: "ship-order", policy: retryPolicy);

        var completion = await group.WaitAsync(TestContext.Current.CancellationToken);
        if (completion.IsFailure && completion.Error?.Code == ErrorCodes.Canceled)
        {
            Assert.Fail("Expected success in the documentation sample.");
        }
        else
        {
            completion.ValueOrThrow();
        }

        Assert.True(completion.IsSuccess);
        Assert.Equal(3, attempts);
    }

    [Fact(Timeout = 15_000)]
    public async Task ErrGroupDocSample_ShouldHandleCancellationResult()
    {
        using var group = new ErrGroup();
        var retryPolicy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(maxAttempts: 1, delay: TimeSpan.Zero));
        var cancellationHandled = false;

        group.Go((ctx, ct) =>
        {
            return Result.RetryWithPolicyAsync(async (_, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return Result.Ok(Unit.Value);
            }, retryPolicy, timeProvider: ctx.TimeProvider, cancellationToken: ct);
        }, stepName: "ship-order", policy: retryPolicy);

        var waitTask = group.WaitAsync(TestContext.Current.CancellationToken);
        group.Cancel();

        var completion = await waitTask;
        if (completion.IsFailure && completion.Error?.Code == ErrorCodes.Canceled)
        {
            cancellationHandled = true;
        }
        else
        {
            completion.ValueOrThrow();
        }

        Assert.True(cancellationHandled);
    }

    [Fact(Timeout = 15_000)]
    public async Task ErrGroupPipeline_ShouldCancelPeersBeforeCompensationCompletes()
    {
        using var group = new ErrGroup();
        var compensationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCompensation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        group.Go((ctx, ct) =>
        {
            Task.Run(async () =>
            {
                compensationStarted.TrySetResult();
                await releaseCompensation.Task;
            });

            return ValueTask.FromResult(Result.Fail<Unit>(Error.From("step-failure", ErrorCodes.Exception)));
        }, stepName: "doc-step", policy: ResultExecutionPolicy.None);

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

        releaseCompensation.TrySetResult();

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Exception, result.Error?.Code);
    }
}
