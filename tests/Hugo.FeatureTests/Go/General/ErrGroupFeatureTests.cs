using Hugo.Policies;

using Unit = Hugo.Go.Unit;

namespace Hugo.Tests;

public class ErrGroupFeatureTests
{
    [Fact]
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

        Assert.True(completion.IsSuccess);
        Assert.Equal(3, attempts);
    }
}
