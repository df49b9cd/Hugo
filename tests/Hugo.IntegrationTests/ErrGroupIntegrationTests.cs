using Hugo.Policies;

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
}
