using System.Threading.Channels;

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
    /// <returns>A <see cref="ValueTask{TResult}"/> that resolves to the aggregated operation results.</returns>
    public static ValueTask<Result<IReadOnlyList<T>>> WhenAll<T>(
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
    /// <returns>A <see cref="ValueTask{TResult}"/> that resolves to the first successful operation result, or an aggregate failure.</returns>
    /// <remarks>
    /// Once a successful result is observed, failures or cancellations from remaining operations do not alter the returned result.
    /// </remarks>
    public static ValueTask<Result<T>> WhenAny<T>(
        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) => WhenAnyInternal(operations, policy, timeProvider ?? TimeProvider.System, cancellationToken);

    private static async ValueTask<Result<IReadOnlyList<T>>> WhenAllInternal<T>(
        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> operations,
        ResultExecutionPolicy? policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var operationList = operations as IList<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> ?? [.. operations];
        if (operationList.Count == 0)
        {
            return Ok<IReadOnlyList<T>>([]);
        }

        var effectivePolicy = policy ?? ResultExecutionPolicy.None;
        var pipelineScope = new CompensationScope();
        var fanOut = new ValueTask<PipelineOperationResult<T>>[operationList.Count];
        var results = new PipelineOperationResult<T>[operationList.Count];
        var completed = new bool[operationList.Count];

        for (var i = 0; i < operationList.Count; i++)
        {
            var operation = operationList[i];
            if (operation is null)
            {
                throw new ArgumentNullException(nameof(operations), "Operation delegate cannot be null.");
            }

#pragma warning disable CA2012 // ValueTasks are stored to enable concurrent awaiting.
            fanOut[i] = ExecuteWithPolicyAsync(operation, $"whenall[{i}]", effectivePolicy, timeProvider, cancellationToken);
#pragma warning restore CA2012
        }

        try
        {
            for (var i = 0; i < fanOut.Length; i++)
            {
                results[i] = await fanOut[i].ConfigureAwait(false);
                completed[i] = true;
            }
        }
        catch (OperationCanceledException oce)
        {
            var cancellationError = await BuildWhenAllCancellationErrorAsync(
                fanOut,
                results,
                completed,
                pipelineScope,
                effectivePolicy,
                oce,
                cancellationToken).ConfigureAwait(false);
            return Fail<IReadOnlyList<T>>(cancellationError);
        }

        var values = new List<T>(results.Length);
        var errors = new List<Error>();

        for (var i = 0; i < results.Length; i++)
        {
            if (!completed[i])
            {
                continue;
            }

            var entry = results[i];
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

        var compensationCancellation = cancellationToken.IsCancellationRequested ? CancellationToken.None : cancellationToken;
        var compensationError = await RunCompensationAsync(effectivePolicy, pipelineScope, compensationCancellation).ConfigureAwait(false);
        if (compensationError is not null)
        {
            errors.Add(compensationError);
        }

        if (cancellationToken.IsCancellationRequested && errors.TrueForAll(error => error?.Code == ErrorCodes.Canceled))
        {
            CancellationToken? cancellationSource = cancellationToken.CanBeCanceled ? cancellationToken : null;
            return Fail<IReadOnlyList<T>>(Error.Canceled(token: cancellationSource));
        }

        var aggregate = errors.Count == 1
            ? errors[0]
            : Error.Aggregate("One or more operations failed.", [.. errors]);

        return Fail<IReadOnlyList<T>>(aggregate);
    }

    private static async ValueTask<Result<T>> WhenAnyInternal<T>(
        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> operations,
        ResultExecutionPolicy? policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var operationList = operations as IList<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> ?? [.. operations];
        if (operationList.Count == 0)
        {
            return Fail<T>(Error.From("No operations provided for WhenAny.", ErrorCodes.Validation));
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return Fail<T>(Error.Canceled(token: cancellationToken));
        }

        var effectivePolicy = policy ?? ResultExecutionPolicy.None;
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var compensationScope = new CompensationScope();
        var errors = new List<Error>();
        PipelineOperationResult<T>? winner = null;

        async Task<(int Index, PipelineOperationResult<T> Outcome)> ExecuteOperationAsync(int operationIndex, Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>> operation)
        {
            var operationScope = new CompensationScope();
            var stepName = $"whenany[{operationIndex}]";
            var outcome = await ExecuteWithPolicyAsync(operation, stepName, effectivePolicy, timeProvider, linkedCts.Token, operationScope).ConfigureAwait(false);
            return (operationIndex, outcome);
        }

        var running = new List<Task<(int Index, PipelineOperationResult<T> Outcome)>>(operationList.Count);
        for (var i = 0; i < operationList.Count; i++)
        {
            var operationIndex = i;
            var operation = operationList[operationIndex] ?? throw new ArgumentNullException(nameof(operations), "Operation delegate cannot be null.");
            running.Add(Task.Run(async () => await ExecuteOperationAsync(operationIndex, operation).ConfigureAwait(false), CancellationToken.None));
        }

        while (running.Count > 0)
        {
            var completedTask = await Task.WhenAny(running).ConfigureAwait(false);
            running.Remove(completedTask);

            var entry = await completedTask.ConfigureAwait(false);
            var result = entry.Outcome;

            if (result.Result.IsSuccess)
            {
                if (winner is null)
                {
                    winner = result;
                    errors.Clear();
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                    continue;
                }

                var compensationError = await RunCompensationAsync(effectivePolicy, result.Compensation, cancellationToken).ConfigureAwait(false);
                if (compensationError is not null)
                {
                    errors.Add(compensationError);
                }
                continue;
            }

            if (result.Result.Error is { Code: ErrorCodes.Canceled })
            {
                if (winner is not null)
                {
                    var cancellationCompensationError = await RunCompensationAsync(effectivePolicy, result.Compensation, cancellationToken).ConfigureAwait(false);
                    if (cancellationCompensationError is not null)
                    {
                        errors.Add(cancellationCompensationError);
                    }
                }
                else
                {
                    result.Compensation.Clear();
                }

                continue;
            }

            result.Compensation.Clear();
            if (winner is null)
            {
                errors.Add(result.Result.Error ?? Error.Unspecified());
            }
        }

        if (winner is null)
        {
            if (errors.Count == 0)
            {
                errors.Add(Error.From("All operations were canceled.", ErrorCodes.Canceled));
            }

            var aggregate = errors.Count == 1
                ? errors[0]
                : Error.Aggregate("All operations failed.", [.. errors]);

            return Fail<T>(aggregate);
        }

        var compensationFailure = await RunCompensationAsync(effectivePolicy, compensationScope, cancellationToken).ConfigureAwait(false);
        if (compensationFailure is not null)
        {
            errors.Add(compensationFailure);
        }

        if (errors.Count > 0)
        {
            var aggregate = errors.Count == 1
                ? errors[0]
                : Error.Aggregate("One or more secondary operations failed.", [.. errors]);
            return Fail<T>(aggregate);
        }

        return winner.Value.Result;
    }

    private static async ValueTask<Error> BuildWhenAllCancellationErrorAsync<T>(
        ValueTask<PipelineOperationResult<T>>[] operations,
        PipelineOperationResult<T>[] results,
        bool[] completed,
        CompensationScope pipelineScope,
        ResultExecutionPolicy policy,
        OperationCanceledException exception,
        CancellationToken fallbackToken)
    {
        var partialErrors = new List<Error>();

        for (var i = 0; i < operations.Length; i++)
        {
            if (completed[i])
            {
                var entry = results[i];
                if (entry.Result.IsSuccess)
                {
                    pipelineScope.Absorb(entry.Compensation);
                }
                else
                {
                    entry.Compensation.Clear();
                    partialErrors.Add(entry.Result.Error ?? Error.Unspecified());
                }

                continue;
            }

            try
            {
                var entry = await operations[i].ConfigureAwait(false);
                completed[i] = true;
                if (entry.Result.IsSuccess)
                {
                    pipelineScope.Absorb(entry.Compensation);
                }
                else
                {
                    entry.Compensation.Clear();
                    partialErrors.Add(entry.Result.Error ?? Error.Unspecified());
                }
            }
            catch (OperationCanceledException)
            {
                partialErrors.Add(Error.Canceled());
            }
            catch (Exception ex)
            {
                partialErrors.Add(Error.FromException(ex));
            }
        }

        var compensationError = await RunCompensationAsync(policy, pipelineScope, CancellationToken.None).ConfigureAwait(false);
        if (compensationError is not null)
        {
            partialErrors.Add(compensationError);
        }

        var cancellationToken = exception.CancellationToken.CanBeCanceled ? exception.CancellationToken : fallbackToken;
        var cancellationError = Error.Canceled(token: cancellationToken);
        if (partialErrors.Count > 0)
        {
            cancellationError = cancellationError.WithMetadata("whenall.partialFailures", partialErrors.ToArray());
        }

        return cancellationError;
    }

    internal static async ValueTask<PipelineOperationResult<T>> ExecuteWithPolicyAsync<T>(
        Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>> operation,
        string stepName,
        ResultExecutionPolicy policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken,
        CompensationScope? sharedScope = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var retryPolicy = policy.EffectiveRetry;
        var retryState = retryPolicy.CreateState(timeProvider);
        retryState.SetActivityId(stepName);

        while (true)
        {
            var attemptScope = sharedScope ?? new CompensationScope();
            var context = new ResultPipelineStepContext(stepName, attemptScope, timeProvider, cancellationToken);
            Result<T> result;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                result = await operation(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                return new PipelineOperationResult<T>(Fail<T>(Error.Canceled(token: oce.CancellationToken)), attemptScope);
        }
#pragma warning disable CA1031 // We need to surface unexpected exceptions as failure results rather than throwing.
            catch (Exception ex)
            {
                return new PipelineOperationResult<T>(Fail<T>(Error.FromException(ex)), attemptScope);
            }
#pragma warning restore CA1031

            if (result.IsSuccess)
            {
                return new PipelineOperationResult<T>(result, attemptScope);
            }

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
                await TimeProviderDelay.WaitAsync(timeProvider, delay, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (decision.ScheduledAt is { } scheduledAt)
            {
                var wait = scheduledAt - timeProvider.GetUtcNow();
                if (wait > TimeSpan.Zero)
                {
                    await TimeProviderDelay.WaitAsync(timeProvider, wait, cancellationToken).ConfigureAwait(false);
                }
            }

            if (sharedScope is not null)
            {
                attemptScope.Clear();
            }
        }
    }

    internal static async ValueTask<Error?> RunCompensationAsync(ResultExecutionPolicy policy, CompensationScope scope, CancellationToken cancellationToken)
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
