using Hugo.Policies;


namespace Hugo.Tests;

public class GoRaceValueTaskAsyncIntegrationTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask RaceValueTaskAsync_ShouldRespectRetryPoliciesPerOperation()
    {
        var attempts = 0;

        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
        {
            async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(5), ct);
                attempts++;
                if (attempts < 3)
                {
                    return Result.Fail<int>(Error.From($"attempt-{attempts}", ErrorCodes.Validation));
                }

                return Result.Ok(99);
            },
            static async ct =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                return Result.Ok(7);
            }
        };

        var policy = ResultExecutionBuilders.FixedRetryPolicy(
            attempts: 3,
            delay: TimeSpan.FromMilliseconds(1));

        using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
        var result = await Go.RaceAsync(
            operations,
            policy,
            cancellationToken: cts.Token);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(99);
        attempts.ShouldBe(3);
    }
}
