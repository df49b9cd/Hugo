using Hugo.Policies;
using Hugo.Sagas;


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

    [Fact(Timeout = 15_000)]
    public async ValueTask ExecuteAsync_ShouldFailWhenNoStepsConfigured()
    {
        var builder = new ResultSagaBuilder();

        var result = await builder.ExecuteAsync(ResultExecutionPolicy.None, cancellationToken: TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error?.Code.ShouldBe(ErrorCodes.Validation);
        result.Error?.Message.ShouldContain("no steps");
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ExecuteAsync_ShouldRespectCancellationBeforeFirstStep()
    {
        var executed = false;
        var builder = new ResultSagaBuilder()
            .AddStep("first", (context, _) =>
            {
                executed = true;
                return ValueTask.FromResult(Result.Ok(context.State.Data.Count));
            });

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(async () =>
            await builder.ExecuteAsync(ResultExecutionPolicy.None, cancellationToken: cts.Token));

        executed.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async ValueTask ExecuteAsync_ShouldStoreResultsUnderCustomKey()
    {
        var builder = new ResultSagaBuilder()
            .AddStep("alpha", static (_, _) => ValueTask.FromResult(Result.Ok("payload")), resultKey: "custom")
            .AddStep("inspect", (context, _) =>
            {
                context.State.TryGet("custom", out string value).ShouldBeTrue();
                context.State.TryGet("alpha", out _).ShouldBeFalse();
                return ValueTask.FromResult(Result.Ok(value + "-seen"));
            }, resultKey: "final");

        var result = await builder.ExecuteAsync(ResultExecutionPolicy.None, cancellationToken: TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Data.ShouldContainKeyAndValue("custom", "payload");
        result.Value.Data.ShouldContainKeyAndValue("final", "payload-seen");
    }

    [Fact(Timeout = 15_000)]
    public void AddStep_ShouldThrow_WhenNameIsMissing()
    {
        var builder = new ResultSagaBuilder();

        Should.Throw<ArgumentException>(() =>
            builder.AddStep<int>(" ", (_, _) => ValueTask.FromResult(Result.Ok(1))));
    }

    [Fact(Timeout = 15_000)]
    public void AddStep_ShouldThrow_WhenOperationIsNull()
    {
        var builder = new ResultSagaBuilder();

        Should.Throw<ArgumentNullException>(() =>
            builder.AddStep<int>("step", null!));
    }
}
