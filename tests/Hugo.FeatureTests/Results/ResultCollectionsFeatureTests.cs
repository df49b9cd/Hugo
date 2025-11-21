using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;


namespace Hugo.Tests;

public sealed class ResultCollectionsFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask SequenceAsync_ShouldAggregateSuccessfulResults()
    {
        async IAsyncEnumerable<Result<int>> Source([EnumeratorCancellation] CancellationToken ct)
        {
            yield return Result.Ok(1);
            await Task.Delay(5, ct);
            yield return Result.Ok(2);
            await Task.Delay(5, ct);
        }

        var result = await Result.SequenceAsync(Source(TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe([1, 2]);
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask TraverseAsync_ShouldSurfaceSelectorExceptionsAsErrors()
    {
        async IAsyncEnumerable<int> Source([EnumeratorCancellation] CancellationToken ct)
        {
            yield return 1;
            await Task.Delay(5, ct);
            yield return 2;
        }

        var result = await Result.TraverseAsync<int, int>(
            Source(TestContext.Current.CancellationToken),
            (value, _) => value == 2
                ? throw new InvalidOperationException("feature-traverse-failure")
                : ValueTask.FromResult(Result.Ok(value * 10)),
            TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
        result.Error!.Code.ShouldBe(ErrorCodes.Exception);
    }
}
