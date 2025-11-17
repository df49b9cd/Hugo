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
}
