using Hugo.Policies;

namespace Hugo;

public static partial class Result
{
    /// <summary>
    /// Executes all pipeline operations, aggregating successful results or combining failures using the optional execution policy.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="operations">The operations to execute.</param>
    /// <param name="policy">The optional execution policy applied to each operation.</param>
    /// <param name="cancellationToken">The token used to cancel execution.</param>
    /// <param name="timeProvider">The optional time provider used for policy timing.</param>
    /// <returns>A task that resolves to the aggregated operation results.</returns>
    public static Task<Result<IReadOnlyList<T>>> WhenAll<T>(
        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) => WhenAllInternal(operations, policy, timeProvider ?? TimeProvider.System, cancellationToken);

    /// <summary>
    /// Executes the supplied operations until one succeeds, cancelling the remaining work and applying the optional execution policy.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="operations">The operations to execute.</param>
    /// <param name="policy">The optional execution policy applied to each operation.</param>
    /// <param name="cancellationToken">The token used to cancel execution.</param>
    /// <param name="timeProvider">The optional time provider used for policy timing.</param>
    /// <returns>A task that resolves to the first successful operation result, or an aggregate failure.</returns>
    public static Task<Result<T>> WhenAny<T>(
        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) => WhenAnyInternal(operations, policy, timeProvider ?? TimeProvider.System, cancellationToken);

    private static async Task<Result<IReadOnlyList<T>>> WhenAllInternal<T>(
        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> operations,
        ResultExecutionPolicy? policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var operationList = operations as IList<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> ?? operations.ToList();
        if (operationList.Count == 0)
        {
            return Ok<IReadOnlyList<T>>(Array.Empty<T>());
        }

        var effectivePolicy = policy ?? ResultExecutionPolicy.None;
        var pipelineScope = new CompensationScope();
        var tasks = new Task<PipelineOperationResult<T>>[operationList.Count];
        for (var i = 0; i < operationList.Count; i++)
        {
            var operation = operationList[i];
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operations), "Operation delegate cannot be null.");
            }

            tasks[i] = ExecuteWithPolicyAsync(operation, $"whenall[{i}]", effectivePolicy, timeProvider, cancellationToken);
        }

        PipelineOperationResult<T>[] results;
        try
        {
            results = await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            await RunCompensationAsync(effectivePolicy, pipelineScope, cancellationToken).ConfigureAwait(false);
            return Fail<IReadOnlyList<T>>(Error.Canceled(token: oce.CancellationToken));
        }

        var values = new List<T>(results.Length);
        var errors = new List<Error>();

        foreach (var entry in results)
        {
            if (entry.Result.IsSuccess)
            {
                pipelineScope.Absorb(entry.Compensation);
                values.Add(entry.Result.Value);
                continue;
            }

            entry.Compensation.Clear();
            errors.Add(entry.Result.Error ?? Error.Unspecified());
        }

        if (errors.Count == 0)
        {
            pipelineScope.Clear();
            return Ok<IReadOnlyList<T>>(values);
        }

        var compensationError = await RunCompensationAsync(effectivePolicy, pipelineScope, cancellationToken).ConfigureAwait(false);
        if (compensationError is not null)
        {
            errors.Add(compensationError);
        }

        var aggregate = errors.Count == 1
            ? errors[0]
            : Error.Aggregate("One or more operations failed.", errors.ToArray());

        return Fail<IReadOnlyList<T>>(aggregate);
    }

    private static async Task<Result<T>> WhenAnyInternal<T>(
        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> operations,
        ResultExecutionPolicy? policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var operationList = operations as IList<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> ?? operations.ToList();
        if (operationList.Count == 0)
        {
            return Fail<T>(Error.From("No operations provided for WhenAny.", ErrorCodes.Validation));
        }

        var effectivePolicy = policy ?? ResultExecutionPolicy.None;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var compensationScope = new CompensationScope();
        var tasks = new List<Task<PipelineOperationResult<T>>>(operationList.Count);
        for (var i = 0; i < operationList.Count; i++)
        {
            var operation = operationList[i];
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operations), "Operation delegate cannot be null.");
            }

            tasks.Add(ExecuteWithPolicyAsync(operation, $"whenany[{i}]", effectivePolicy, timeProvider, linkedCts.Token));
        }

        var errors = new List<Error>();
        PipelineOperationResult<T>? winner = null;

        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(completed);

            PipelineOperationResult<T> result;
            try
            {
                result = await completed.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Task cancelled due to linked token; ignore and continue.
                continue;
            }

            if (result.Result.IsSuccess)
            {
                if (winner is null)
                {
                    winner = result;
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                    continue;
                }

                // Successful but not the selected winner. Schedule compensation.
                compensationScope.Absorb(result.Compensation);
                continue;
            }

            if (result.Result.Error is { Code: ErrorCodes.Canceled })
            {
                continue;
            }

            result.Compensation.Clear();
            errors.Add(result.Result.Error ?? Error.Unspecified());
        }

        // Ensure any pending operations are awaited
        if (tasks.Count > 0)
        {
            try
            {
                var pending = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach (var entry in pending)
                {
                    if (entry.Result.IsSuccess)
                    {
                        compensationScope.Absorb(entry.Compensation);
                    }
                    else if (entry.Result.Error is not null && entry.Result.Error.Code != ErrorCodes.Canceled)
                    {
                        errors.Add(entry.Result.Error);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Ignore cancellation from linked token.
            }
        }

        if (winner is null)
        {
            var compensationError = await RunCompensationAsync(effectivePolicy, compensationScope, cancellationToken).ConfigureAwait(false);
            if (compensationError is not null)
            {
                errors.Add(compensationError);
            }

            if (errors.Count == 0)
            {
                errors.Add(Error.From("All operations were canceled.", ErrorCodes.Canceled));
            }

            var aggregate = errors.Count == 1
                ? errors[0]
                : Error.Aggregate("All operations failed.", errors.ToArray());

            return Fail<T>(aggregate);
        }

        // Run compensation for all non-selected successful operations
        var compensationFailure = await RunCompensationAsync(effectivePolicy, compensationScope, cancellationToken).ConfigureAwait(false);
        if (compensationFailure is not null)
        {
            errors.Add(compensationFailure);
        }

        if (errors.Count > 0)
        {
            var aggregate = errors.Count == 1
                ? errors[0]
                : Error.Aggregate("One or more secondary operations failed.", errors.ToArray());
            return Fail<T>(aggregate);
        }

        return winner.Value.Result;
    }

    internal static async Task<PipelineOperationResult<T>> ExecuteWithPolicyAsync<T>(
        Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>> operation,
        string stepName,
        ResultExecutionPolicy policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var retryPolicy = policy.EffectiveRetry;
        var retryState = retryPolicy.CreateState(timeProvider);
        retryState.SetActivityId(stepName);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attemptScope = new CompensationScope();
            var context = new ResultPipelineStepContext(stepName, attemptScope, timeProvider);
            Result<T> result;

            try
            {
                result = await operation(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                attemptScope.Clear();
                return new PipelineOperationResult<T>(Fail<T>(Error.Canceled(token: oce.CancellationToken)), attemptScope);
            }
#pragma warning disable CA1031 // We need to surface unexpected exceptions as failure results rather than throwing.
            catch (Exception ex)
            {
                attemptScope.Clear();
                return new PipelineOperationResult<T>(Fail<T>(Error.FromException(ex)), attemptScope);
            }
#pragma warning restore CA1031

            if (result.IsSuccess)
            {
                return new PipelineOperationResult<T>(result, attemptScope);
            }

            attemptScope.Clear();

            var error = result.Error ?? Error.Unspecified();
            if (error.Code == ErrorCodes.Canceled)
            {
                return new PipelineOperationResult<T>(result, attemptScope);
            }

            var decision = await retryPolicy.EvaluateAsync(retryState, error, cancellationToken).ConfigureAwait(false);
            if (!decision.ShouldRetry)
            {
                return new PipelineOperationResult<T>(result, attemptScope);
            }

            if (decision.Delay is { } delay && delay > TimeSpan.Zero)
            {
                await timeProvider.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (decision.ScheduledAt is { } scheduledAt)
            {
                var wait = scheduledAt - timeProvider.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await timeProvider.DelayAsync(wait, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    internal static async Task<Error?> RunCompensationAsync(ResultExecutionPolicy policy, CompensationScope scope, CancellationToken cancellationToken)
    {
        if (!scope.HasActions)
        {
            return null;
        }

        var actions = scope.Capture();
        if (actions.Count == 0)
        {
            return null;
        }

        try
        {
            var context = new CompensationContext(actions, cancellationToken);
            await policy.EffectiveCompensation.ExecuteAsync(context).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException oce)
        {
            return Error.Canceled(token: oce.CancellationToken);
        }
#pragma warning disable CA1031 // Compensation errors must be captured and reported via Error.
        catch (Exception ex)
        {
            return Error.FromException(ex);
        }
#pragma warning restore CA1031
    }

    internal readonly record struct PipelineOperationResult<T>(Result<T> Result, CompensationScope Compensation);
}
