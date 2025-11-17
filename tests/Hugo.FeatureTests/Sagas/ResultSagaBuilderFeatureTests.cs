using Hugo.Policies;
using Hugo.Sagas;
using Shouldly;

namespace Hugo.Tests;

public class ResultSagaBuilderFeatureTests
{
    [Fact(Timeout = 15_000)]
    public async ValueTask ExecuteAsync_ShouldPersistStateAcrossSteps()
    {
        var builder = new ResultSagaBuilder()
            .AddStep("first", static (_, _) => ValueTask.FromResult(Result.Ok("alpha")), resultKey: "first")
            .AddStep("second", static (context, _) =>
            {
                context.State.TryGet("first", out string value).ShouldBeTrue();
                return ValueTask.FromResult(Result.Ok(value + "-bravo"));
            }, resultKey: "second");

        var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
        var result = await builder.ExecuteAsync(policy, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Data.ShouldContainKeyAndValue("first", "alpha");
        result.Value.Data.ShouldContainKeyAndValue("second", "alpha-bravo");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ExecuteAsync_ShouldRunCompensation_WhenStepFails()
    {
        var calls = new List<string>();
        var builder = new ResultSagaBuilder()
            .AddStep("prepare", (context, _) =>
            {
                calls.Add("prepare");
                context.RegisterCompensation<List<string>>(calls, static (state, _) =>
                {
                    state.Add("compensated");
                    return ValueTask.CompletedTask;
                });
                return ValueTask.FromResult(Result.Ok(1));
            })
            .AddStep("crash", static (_, _) =>
                ValueTask.FromResult(Result.Fail<int>(Error.From("boom", ErrorCodes.Exception))));

        var policy = ResultExecutionPolicy.None.WithCompensation(ResultCompensationPolicy.SequentialReverse);
        var result = await builder.ExecuteAsync(policy, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        calls.ShouldContain("prepare");
        calls.ShouldContain("compensated");
        calls.IndexOf("compensated").ShouldBeGreaterThan(calls.IndexOf("prepare"));
        result.Error?.Code.ShouldBe(ErrorCodes.Exception);
    }
}
