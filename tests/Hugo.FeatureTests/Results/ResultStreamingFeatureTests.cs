using System.Runtime.CompilerServices;

using Shouldly;

namespace Hugo.Tests;

public sealed class ResultStreamingFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async Task MapStreamAsync_ShouldShortCircuitFeatureStreamOnFailure()
    {
        var collected = new List<int>();

        await foreach (var outcome in Result.MapStreamAsync(Values(TestContext.Current.CancellationToken), Selector, TestContext.Current.CancellationToken))
        {
            if (outcome.IsFailure)
            {
                break;
            }

            collected.Add(outcome.Value);
        }

        collected.ShouldBe([10, 20]);
    }

    private static async IAsyncEnumerable<int> Values([EnumeratorCancellation] CancellationToken token)
    {
        yield return 1;
        await Task.Yield();
        yield return 2;
        await Task.Yield();
        yield return 3;
        await Task.Delay(5, token);
        yield return 4;
    }

    private static ValueTask<Result<int>> Selector(int value, CancellationToken token)
    {
        if (value == 3)
        {
            return ValueTask.FromResult(Result.Fail<int>(Error.From("feature-fail")));
        }

        return ValueTask.FromResult(Result.Ok(value * 10));
    }
}
