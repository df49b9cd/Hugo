using System.Threading;

using Hugo.Policies;
using Unit = Hugo.Go.Unit;

namespace Hugo;

/// <summary>
/// Bridges <see cref="ErrGroup"/> with <see cref="ResultPipelineStepContext"/>.
/// </summary>
public static class ResultPipelineErrGroupExtensions
{
    public static void Go(
        this ErrGroup group,
        ResultPipelineStepContext parentContext,
        Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<Unit>>> work,
        string? stepName = null,
        ResultExecutionPolicy? policy = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(parentContext);
        ArgumentNullException.ThrowIfNull(work);

        string resolvedName = string.IsNullOrWhiteSpace(stepName)
            ? $"{parentContext.StepName}.errgroup"
            : stepName!;

        group.Go(
            async (ctx, token) =>
            {
                var scope = new CompensationScope();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentContext.CancellationToken, token);
                var childContext = new ResultPipelineStepContext(resolvedName, scope, parentContext.TimeProvider, linkedCts.Token);
                var result = await work(childContext, linkedCts.Token).ConfigureAwait(false);
                parentContext.AbsorbResult(result);
                if (scope.HasActions)
                {
                    parentContext.AbsorbCompensation(scope);
                }

                return result;
            },
            resolvedName,
            policy,
            timeProvider ?? parentContext.TimeProvider);
    }
}
