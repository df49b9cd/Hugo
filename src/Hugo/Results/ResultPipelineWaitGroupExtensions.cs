using System.Threading;

using Hugo.Policies;

namespace Hugo;

/// <summary>
/// Provides helpers that bridge <see cref="WaitGroup"/> with <see cref="ResultPipelineStepContext"/>.
/// </summary>
public static class ResultPipelineWaitGroupExtensions
{
    /// <summary>
    /// Runs a pipeline-aware delegate under the specified <see cref="WaitGroup"/>, absorbing compensations into the parent context.
    /// </summary>
    public static void Go(
        this WaitGroup waitGroup,
        ResultPipelineStepContext parentContext,
        Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<Go.Unit>>> work,
        string? stepName = null)
    {
        ArgumentNullException.ThrowIfNull(waitGroup);
        ArgumentNullException.ThrowIfNull(parentContext);
        ArgumentNullException.ThrowIfNull(work);

        waitGroup.Go(async () =>
        {
            var scope = new CompensationScope();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(parentContext.CancellationToken);
            var childName = string.IsNullOrWhiteSpace(stepName)
                ? $"{parentContext.StepName}.waitgroup"
                : stepName;

            var childContext = new ResultPipelineStepContext(childName, scope, parentContext.TimeProvider, linkedCts.Token);
            Result<Go.Unit> result;

            try
            {
                result = await work(childContext, linkedCts.Token).ConfigureAwait(false);
            }
            finally
            {
                linkedCts.Cancel();
            }

            parentContext.AbsorbResult(result);
            if (scope.HasActions)
            {
                parentContext.AbsorbCompensation(scope);
            }
        }, parentContext.CancellationToken);
    }
}
