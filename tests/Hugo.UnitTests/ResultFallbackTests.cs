using Hugo.Policies;
using Shouldly;

using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

public class ResultFallbackTests
{
    [Fact(Timeout = 15_000)]
    public void ResultFallbackTier_ShouldThrow_WhenOperationsEmpty() => Should.Throw<ArgumentException>(static () => new ResultFallbackTier<int>("empty", []));

    [Fact(Timeout = 15_000)]
    public async ValueTask TieredFallbackAsync_ShouldReturnValidationError_WhenNoTiersProvided()
    {
        var result = await Result.TieredFallbackAsync(Array.Empty<ResultFallbackTier<int>>(), cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
        result.Error?.Message.ShouldBe("No fallback tiers were provided.");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TieredFallbackAsync_ShouldReturnPrimaryResult()
    {
        var tiers = new[]
        {
            ResultFallbackTier<int>.From("primary", static _ => ValueTask.FromResult(Result.Ok(42)))
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(42);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TieredFallbackAsync_ShouldUseSecondaryTierWhenPrimaryFails()
    {
        var tiers = new[]
        {
            ResultFallbackTier<int>.From(
                "primary",
                static _ => ValueTask.FromResult(Result.Fail<int>(Error.From("primary failed", ErrorCodes.Validation)))),
            ResultFallbackTier<int>.From("secondary", static _ => ValueTask.FromResult(Result.Ok(100)))
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(100);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TieredFallbackAsync_ShouldAggregateErrorsWhenAllTiersFail()
    {
        var tiers = new[]
        {
            ResultFallbackTier<int>.From(
                "primary",
                static _ => ValueTask.FromResult(Result.Fail<int>(Error.From("primary failed", ErrorCodes.Validation)))),
            ResultFallbackTier<int>.From(
                "secondary",
                static _ => ValueTask.FromResult(Result.Fail<int>(Error.From("secondary failed", "error.secondary"))))
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Message.ShouldBe("Fallback pipeline exhausted all tiers.");
        result.Error.ShouldNotBeNull();
        result.Error!.Metadata.TryGetValue("errors", out var nestedObj).ShouldBeTrue();
        var nestedErrors = nestedObj.ShouldBeOfType<Error[]>();
        nestedErrors.Length.ShouldBe(2);
        static error =>
        {
            return error.Metadata.TryGetValue("fallbackTier", out var value) && string.Equals("primary", value?.ToString(), StringComparison.Ordinal.ShouldContain(nestedErrors);
        });
        static error =>
        {
            return error.Metadata.TryGetValue("fallbackTier", out var value) && string.Equals("secondary", value?.ToString(), StringComparison.Ordinal.ShouldContain(nestedErrors);
        });
        nestedErrors.ShouldAllBe(error => error.Metadata.ContainsKey("strategyIndex"));
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TieredFallbackAsync_ShouldPropagateCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var tiers = new[]
        {
            ResultFallbackTier<int>.From("primary", static _ => ValueTask.FromResult(Result.Ok(1)))
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: cts.Token);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TieredFallbackAsync_ShouldCancelRemainingStrategiesAfterSuccess()
    {
        var cancellationCount = 0;

        var tiers = new[]
        {
            new ResultFallbackTier<int>(
                "primary",
                [
                    async (ctx, ct) =>
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
                        return Result.Ok(7);
                    },
                    async (_, ct) =>
                    {
                        try
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), ct);
                        }
                        catch (OperationCanceledException)
                        {
                            Interlocked.Increment(ref cancellationCount);
                            throw;
                        }

                        return Result.Ok(0);
                    }
                ])
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(7);
        cancellationCount > 0.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ErrGroup_ShouldRespectRetryPolicyWhenRunningPipelineSteps()
    {
        using var group = new ErrGroup();
        var attempts = 0;
        var provider = new FakeTimeProvider();
        var policy = ResultExecutionPolicy.None.WithRetry(ResultRetryPolicy.FixedDelay(2, TimeSpan.Zero));

        group.Go((ctx, ct) =>
        {
            attempts++;
            if (attempts < 2)
            {
                return ValueTask.FromResult(Result.Fail<Unit>(Error.From("transient", ErrorCodes.Timeout)));
            }

            return ValueTask.FromResult(Result.Ok(Unit.Value));
        }, policy: policy, timeProvider: provider);

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        attempts.ShouldBe(2);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ErrGroup_ShouldSurfaceFailureWhenAllAttemptsFail()
    {
        using var group = new ErrGroup();
        var provider = new FakeTimeProvider();
        var policy = ResultExecutionPolicy.None
            .WithRetry(ResultRetryPolicy.FixedDelay(1, TimeSpan.Zero))
            .WithCompensation(ResultCompensationPolicy.SequentialReverse);

        group.Go(static (ctx, ct) =>
        {
            return ValueTask.FromResult(Result.Fail<Unit>(Error.From("fatal", ErrorCodes.Validation)));
        }, policy: policy, timeProvider: provider);

        var result = await group.WaitAsync(TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
    }
}
