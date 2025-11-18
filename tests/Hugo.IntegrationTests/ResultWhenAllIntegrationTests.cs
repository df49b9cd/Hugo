using Hugo.Policies;
using Shouldly;

namespace Hugo.Tests;

public sealed class ResultWhenAllIntegrationTests
{
    [Fact(Timeout = 30_000)]
    public async Task WhenAll_ShouldAggregateCancellationAndRunCompensation()
    {
        using var cts = new CancellationTokenSource();
        var compensationInvocations = new List<string>();

        var policy = ResultExecutionPolicy.None.WithCompensation(new ResultCompensationPolicy(context =>
        {
            compensationInvocations.Add("policy");
            return ValueTask.CompletedTask;
        }));

        var operations = new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<int>>>[]
        {
            async (ctx, token) =>
            {
                ctx.RegisterCompensation(_ =>
                {
                    compensationInvocations.Add("first");
                    return ValueTask.CompletedTask;
                });

                await Task.Delay(15, CancellationToken.None);
                return Result.Ok(1);
            },
            async (ctx, token) =>
            {
                ctx.RegisterCompensation(_ =>
                {
                    compensationInvocations.Add("second");
                    return ValueTask.CompletedTask;
                });

                await Task.Delay(30, TestContext.Current.CancellationToken);
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return Result.Ok(2);
            }
        };

        var result = await Result.WhenAll(operations, policy: policy, cancellationToken: cts.Token);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldNotBeNull();
        result.Error!.Code.ShouldBe(ErrorCodes.Canceled);
    }
}
