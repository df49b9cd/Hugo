using System.Threading;
using System.Threading.Tasks;

using Hugo.Policies;


namespace Hugo.Tests;

public sealed class ResultFallbackFeatureTests
{
    [Fact(Timeout = 20_000)]
    public async Task TieredFallbackAsync_ShouldSkipUnrequestedCancellationsAndUseNextTier()
    {
        var tierOne = new ResultFallbackTier<int>(
            "primary",
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>[]
            {
                (_, _) => ValueTask.FromResult(Result.Fail<int>(Error.Canceled())),
                (_, _) => ValueTask.FromResult(Result.Fail<int>(Error.From("primary-failure")))
            });

        var tierTwo = ResultFallbackTier<int>.From(
            "secondary",
            () => Result.Ok(42));

        var outcome = await Result.TieredFallbackAsync([tierOne, tierTwo], ResultExecutionPolicy.None, TimeProvider.System, TestContext.Current.CancellationToken);

        outcome.IsSuccess.ShouldBeTrue();
        outcome.Value.ShouldBe(42);
    }
}
