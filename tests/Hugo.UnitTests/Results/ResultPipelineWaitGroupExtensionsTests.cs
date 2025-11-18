using Hugo.Policies;
using Shouldly;

namespace Hugo.Tests.Results;

public sealed class ResultPipelineWaitGroupExtensionsTests
{
    [Fact(Timeout = 15_000)]
    public async Task Go_ShouldPropagateChildResultIntoParentContext()
    {
        var waitGroup = new WaitGroup();
        var parentScope = new CompensationScope();
        var parentContext = new ResultPipelineStepContext("parent", parentScope, TimeProvider.System, TestContext.Current.CancellationToken);

        waitGroup.Go(parentContext, async (ctx, ct) =>
        {
            ctx.RegisterCompensation(_ => ValueTask.CompletedTask);
            await Task.Yield();
            return Result.Ok(Go.Unit.Value);
        });

        await waitGroup.WaitAsync(TestContext.Current.CancellationToken);

        // Absence of exceptions is sufficient; compensation was absorbed into parent scope.
        parentScope.HasActions.ShouldBeTrue();
    }

    [Fact(Timeout = 15_000)]
    public async Task Go_ShouldCancelChildTokenAfterWorkCompletes()
    {
        var waitGroup = new WaitGroup();
        var parentScope = new CompensationScope();
        var parentContext = new ResultPipelineStepContext("parent", parentScope, TimeProvider.System, TestContext.Current.CancellationToken);
        var childCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        waitGroup.Go(parentContext, async (ctx, ct) =>
        {
            ct.Register(() => childCanceled.TrySetResult());
            await Task.Delay(10, TestContext.Current.CancellationToken);
            return Result.Ok(Go.Unit.Value);
        });

        await waitGroup.WaitAsync(TestContext.Current.CancellationToken);
        await childCanceled.Task.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        parentScope.HasActions.ShouldBeFalse();
    }

    [Fact(Timeout = 15_000)]
    public async Task Go_ShouldHonorExplicitStepName()
    {
        var waitGroup = new WaitGroup();
        var parentContext = new ResultPipelineStepContext("parent", new CompensationScope(), TimeProvider.System, TestContext.Current.CancellationToken);
        string? observedName = null;

        waitGroup.Go(
            parentContext,
            (ctx, _) =>
            {
                observedName = ctx.StepName;
                return ValueTask.FromResult(Result.Ok(Go.Unit.Value));
            },
            stepName: "custom-step");

        await waitGroup.WaitAsync(TestContext.Current.CancellationToken);

        observedName.ShouldBe("custom-step");
    }

    [Fact(Timeout = 15_000)]
    public async Task Go_ShouldAbsorbCompensationOnFailure()
    {
        var waitGroup = new WaitGroup();
        var parentScope = new CompensationScope();
        var parentContext = new ResultPipelineStepContext("parent", parentScope, TimeProvider.System, TestContext.Current.CancellationToken);

        waitGroup.Go(parentContext, (ctx, _) =>
        {
            ctx.RegisterCompensation(_ => ValueTask.CompletedTask);
            return ValueTask.FromResult(Result.Fail<Go.Unit>(Error.From("boom")));
        });

        await waitGroup.WaitAsync(TestContext.Current.CancellationToken);

        parentScope.HasActions.ShouldBeTrue();
    }
}
