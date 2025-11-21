
namespace Hugo.Tests;

public sealed class GoRaceFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask RaceAsync_ShouldReturnFirstSuccessfulResult()
    {
        var operations = new List<Func<CancellationToken, ValueTask<Result<int>>>>
        {
            static async ct =>
            {
                await Task.Delay(50, ct);
                return Result.Ok(1);
            },
            static ct => ValueTask.FromResult(Result.Ok(99))
        };

        var result = await Go.RaceAsync(operations, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(99);
    }
}
