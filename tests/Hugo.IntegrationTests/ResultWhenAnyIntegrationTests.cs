using Hugo.Policies;
using Shouldly;

namespace Hugo.Tests;

public sealed class ResultWhenAnyIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task WhenAny_ShouldSurfaceCompensationErrorsFromSecondarySuccesses()
    {
        var compensationPolicy = new ResultCompensationPolicy(_ => throw new InvalidOperationException("compensation crash"));
        var policy = ResultExecutionPolicy.None.WithCompensation(compensationPolicy);

        var operations = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>[]
        {
            async ValueTask<Result<int>> (ResultPipelineStepContext context, CancellationToken token) =>
            {
                context.RegisterCompensation(_ => ValueTask.CompletedTask);
                await Task.Delay(15, token);
                return Result.Ok(1);
            },
            async ValueTask<Result<int>> (ResultPipelineStepContext context, CancellationToken token) =>
            {
                context.RegisterCompensation(_ => ValueTask.CompletedTask);
                await Task.Delay(30, token);
                return Result.Ok(2);
            }
        };

        var outcome = await Result.WhenAny<int>(operations, policy: policy, cancellationToken: TestContext.Current.CancellationToken);

        outcome.IsFailure.ShouldBeTrue();
        outcome.Error.ShouldNotBeNull();
        outcome.Error?.Code.ShouldBe(ErrorCodes.Exception);
    }
}
