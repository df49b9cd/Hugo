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
}
