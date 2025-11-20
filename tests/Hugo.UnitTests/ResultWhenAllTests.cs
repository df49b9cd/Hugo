using Hugo.Policies;

using Microsoft.Extensions.Time.Testing;

using Shouldly;

namespace Hugo.Tests;

public sealed class ResultWhenAllTests
{
    [Fact(Timeout = 30_000)]
    public async ValueTask WhenAll_ShouldReturnCancellationWithPartialFailures()
    {
        using var cts = new CancellationTokenSource();

        var operations = new List<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>>
        {
            (_, _) => ValueTask.FromResult(Result.Ok(1)),
            (_, _) => ValueTask.FromResult(Result.Fail<int>(Error.From("secondary"))),
            async (_, token) =>
            {
                await Task.Delay(20, TestContext.Current.CancellationToken);
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return Result.Ok(3);
            }
        };

        var result = await Result.WhenAll(operations, cancellationToken: cts.Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask ExecuteWithPolicyAsync_ShouldApplyDelayAndScheduledRetries()
    {
        var delayPolicy = ResultExecutionBuilders.FixedRetryPolicy(attempts: 2, delay: TimeSpan.FromMilliseconds(25));
        int attempts = 0;

        var delayTask = Result.ExecuteWithPolicyAsync(
            (_, _) =>
            {
                attempts++;
                return attempts == 1
                    ? ValueTask.FromResult(Result.Fail<int>(Error.From("retry")))
                    : ValueTask.FromResult(Result.Ok(10));
            },
            "delay",
            delayPolicy,
            TimeProvider.System,
            CancellationToken.None);

        var delayResult = await delayTask.AsTask();
        delayResult.Result.IsSuccess.ShouldBeTrue();
        attempts.ShouldBe(2);
    }

    [Fact(Timeout = 30_000)]
    public async ValueTask WhenAll_ShouldSurfacePartialFailuresOnCancellation()
    {
        using var cts = new CancellationTokenSource();

        var operations = new List<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>>
        {
            (_, _) => ValueTask.FromResult(Result.Ok(1)),
            (_, _) => ValueTask.FromResult(Result.Fail<int>(Error.From("boom", ErrorCodes.Validation))),
            async (_, token) =>
            {
                await Task.Delay(100, token);
                return Result.Ok(3);
            }
        };

        _ = Task.Run(async () =>
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            cts.Cancel();
        }, TestContext.Current.CancellationToken);

        var result = await Result.WhenAll(operations, cancellationToken: cts.Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
        result.Error!.Code.ShouldBeOneOf(ErrorCodes.Canceled, ErrorCodes.Aggregate);
    }

    [Fact(Timeout = 5_000)]
    public async ValueTask WhenAll_ShouldThrowWhenOperationIsNull()
    {
        var operations = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>?[]
        {
            (_, _) => ValueTask.FromResult(Result.Ok(1)),
            null
        };

        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await Result.WhenAll(operations!, cancellationToken: TestContext.Current.CancellationToken));
    }
}
