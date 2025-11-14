using Hugo.Policies;

using Microsoft.Extensions.Time.Testing;

using static Hugo.Go;

namespace Hugo.Tests;

public class ResultFallbackTests
{
    [Fact(Timeout = 15_000)]
    public void ResultFallbackTier_ShouldThrow_WhenOperationsEmpty()
    {
        Assert.Throws<ArgumentException>(static () => new ResultFallbackTier<int>("empty", Array.Empty<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>>()));
    }

    [Fact(Timeout = 15_000)]
    public async Task TieredFallbackAsync_ShouldReturnValidationError_WhenNoTiersProvided()
    {
        var result = await Result.TieredFallbackAsync(Array.Empty<ResultFallbackTier<int>>(), cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
        Assert.Equal("No fallback tiers were provided.", result.Error?.Message);
    }

    [Fact(Timeout = 15_000)]
    public async Task TieredFallbackAsync_ShouldReturnPrimaryResult()
    {
        var tiers = new[]
        {
            ResultFallbackTier<int>.From("primary", static _ => ValueTask.FromResult(Result.Ok(42)))
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task TieredFallbackAsync_ShouldUseSecondaryTierWhenPrimaryFails()
    {
        var tiers = new[]
        {
            ResultFallbackTier<int>.From(
                "primary",
                static _ => ValueTask.FromResult(Result.Fail<int>(Error.From("primary failed", ErrorCodes.Validation)))),
            ResultFallbackTier<int>.From("secondary", static _ => ValueTask.FromResult(Result.Ok(100)))
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(100, result.Value);
    }

    [Fact(Timeout = 15_000)]
    public async Task TieredFallbackAsync_ShouldAggregateErrorsWhenAllTiersFail()
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

        Assert.True(result.IsFailure);
        Assert.Equal("Fallback pipeline exhausted all tiers.", result.Error?.Message);
        Assert.NotNull(result.Error);
        Assert.True(result.Error!.Metadata.TryGetValue("errors", out var nestedObj));
        var nestedErrors = Assert.IsType<Error[]>(nestedObj);
        Assert.Equal(2, nestedErrors.Length);
        Assert.Contains(nestedErrors, static error =>
        {
            return error.Metadata.TryGetValue("fallbackTier", out var value) && string.Equals("primary", value?.ToString(), StringComparison.Ordinal);
        });
        Assert.Contains(nestedErrors, static error =>
        {
            return error.Metadata.TryGetValue("fallbackTier", out var value) && string.Equals("secondary", value?.ToString(), StringComparison.Ordinal);
        });
        Assert.All(nestedErrors, static error => Assert.True(error.Metadata.ContainsKey("strategyIndex")));
    }

    [Fact(Timeout = 15_000)]
    public async Task TieredFallbackAsync_ShouldPropagateCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await cts.CancelAsync();

        var tiers = new[]
        {
            ResultFallbackTier<int>.From("primary", static _ => ValueTask.FromResult(Result.Ok(1)))
        };

        var result = await Result.TieredFallbackAsync(tiers, cancellationToken: cts.Token);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Canceled, result.Error?.Code);
    }

    [Fact(Timeout = 15_000)]
    public async Task TieredFallbackAsync_ShouldCancelRemainingStrategiesAfterSuccess()
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

        Assert.True(result.IsSuccess);
        Assert.Equal(7, result.Value);
        Assert.True(cancellationCount > 0);
    }

    [Fact(Timeout = 15_000)]
    public async Task ErrGroup_ShouldRespectRetryPolicyWhenRunningPipelineSteps()
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

        Assert.True(result.IsSuccess);
        Assert.Equal(2, attempts);
    }

    [Fact(Timeout = 15_000)]
    public async Task ErrGroup_ShouldSurfaceFailureWhenAllAttemptsFail()
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

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.Validation, result.Error?.Code);
    }
}
