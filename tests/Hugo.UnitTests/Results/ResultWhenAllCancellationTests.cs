using Hugo.Policies;


namespace Hugo.Tests.Results;

public sealed class ResultWhenAllCancellationTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask WhenAll_ShouldReturnCanceledResult_WhenCallerTokenAlreadyCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var invoked = false;
        var operations = new[]
        {
            new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>((_, _) =>
            {
                invoked = true;
                return ValueTask.FromResult(Result.Ok(1));
            })
        };

        var result = await Result.WhenAll(operations, cancellationToken: cts.Token);

        invoked.ShouldBeFalse();
        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Canceled);
    }
}
