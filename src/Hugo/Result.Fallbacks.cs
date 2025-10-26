using System.Collections.Concurrent;

using Hugo.Policies;

namespace Hugo;

/// <summary>
/// Describes a tier of fallback operations evaluated by <see cref="Result.TieredFallbackAsync"/>.
/// </summary>
/// <typeparam name="T">The result value type.</typeparam>
public sealed class ResultFallbackTier<T>
{
    private readonly IReadOnlyList<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> _operations;

    /// <summary>Initializes a new fallback tier using pipeline-aware operations.</summary>
    /// <param name="name">The name assigned to the fallback tier.</param>
    /// <param name="operations">The operations evaluated within the tier.</param>
    public ResultFallbackTier(
        string name,
        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> operations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(operations);

        var list = operations as IList<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> ?? operations.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one fallback operation must be provided.", nameof(operations));
        }

        Name = name;
        _operations = new List<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>>(list);
    }

    /// <summary>Gets the name assigned to the fallback tier.</summary>
    public string Name { get; }

    internal IReadOnlyList<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> Operations => _operations;

    /// <summary>Creates a tier from operations that accept a cancellation token.</summary>
    /// <param name="name">The name assigned to the fallback tier.</param>
    /// <param name="operations">The operations evaluated within the tier.</param>
    /// <returns>A configured fallback tier.</returns>
    public static ResultFallbackTier<T> From(
        string name,
        params Func<CancellationToken, ValueTask<Result<T>>>[] operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var pipelineOperations = operations.Select(operation =>
            (Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>)(
                (_, cancellationToken) => operation(cancellationToken)));

        return new ResultFallbackTier<T>(name, pipelineOperations);
    }

    /// <summary>Creates a tier from synchronous operations.</summary>
    /// <param name="name">The name assigned to the fallback tier.</param>
    /// <param name="operations">The operations evaluated within the tier.</param>
    /// <returns>A configured fallback tier.</returns>
    public static ResultFallbackTier<T> From(
        string name,
        params Func<Result<T>>[] operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        var pipelineOperations = operations.Select(operation =>
            (Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>)(
                (_, _) => ValueTask.FromResult(operation())));

        return new ResultFallbackTier<T>(name, pipelineOperations);
    }
}

public static partial class Result
{
    /// <summary>
    /// Executes the provided fallback tiers in order until one succeeds, using the optional execution policy for retries and compensation.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="tiers">The ordered collection of fallback tiers.</param>
    /// <param name="policy">The optional execution policy applied to each tier.</param>
    /// <param name="cancellationToken">The token used to cancel the fallback execution.</param>
    /// <param name="timeProvider">The optional time provider used for policy timing.</param>
    /// <returns>A result describing the outcome of the fallback orchestration.</returns>
    public static Task<Result<T>> TieredFallbackAsync<T>(
        IEnumerable<ResultFallbackTier<T>> tiers,
        ResultExecutionPolicy? policy = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null) => TieredFallbackInternal(tiers, policy, cancellationToken, timeProvider ?? TimeProvider.System);

    private static async Task<Result<T>> TieredFallbackInternal<T>(
        IEnumerable<ResultFallbackTier<T>> tiers,
        ResultExecutionPolicy? policy,
        CancellationToken cancellationToken,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var tierList = tiers as IList<ResultFallbackTier<T>> ?? tiers.ToList();
        if (tierList.Count == 0)
        {
            return Fail<T>(Error.From("No fallback tiers were provided.", ErrorCodes.Validation));
        }

        var effectivePolicy = policy ?? ResultExecutionPolicy.None;
        var tierErrors = new List<Error>(tierList.Count);

        try
        {
            for (var i = 0; i < tierList.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var tier = tierList[i];
                var tierResult = await ExecuteTierAsync(tier, i, effectivePolicy, timeProvider, cancellationToken).ConfigureAwait(false);
                if (tierResult.IsSuccess)
                {
                    return tierResult;
                }

                var error = tierResult.Error ?? Error.Unspecified();
                if (error.Code == ErrorCodes.Canceled)
                {
                    return Fail<T>(error);
                }

                tierErrors.Add(error);
            }
        }
        catch (OperationCanceledException oce)
        {
            return Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }

        if (tierErrors.Count == 0)
        {
            return Fail<T>(Error.From("Fallback orchestration completed without producing a value.", ErrorCodes.Unspecified));
        }

        if (tierErrors.Count == 1)
        {
            return Fail<T>(tierErrors[0]);
        }

        var aggregate = Error.Aggregate("Fallback pipeline exhausted all tiers.", tierErrors.ToArray());
        return Fail<T>(aggregate);
    }

    private static async Task<Result<T>> ExecuteTierAsync<T>(
        ResultFallbackTier<T> tier,
        int tierIndex,
        ResultExecutionPolicy policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        try
        {
            var operations = tier.Operations;
            if (operations.Count == 0)
            {
                return Fail<T>(DecorateTierError(Error.From("Tier contains no strategies.", ErrorCodes.Validation), tier.Name, tierIndex, null));
            }

            if (operations.Count == 1)
            {
                var single = await ExecuteWithPolicyAsync(operations[0], $"{tier.Name}[0]", policy, timeProvider, cancellationToken).ConfigureAwait(false);
                if (single.Result.IsSuccess)
                {
                    single.Compensation.Clear();
                    return single.Result;
                }

                single.Compensation.Clear();
                return Fail<T>(DecorateTierError(single.Result.Error ?? Error.Unspecified(), tier.Name, tierIndex, 0));
            }

            var successSource = new TaskCompletionSource<Result<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var errors = new ConcurrentBag<Error>();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var group = new ErrGroup(linkedCts.Token);

            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i];
                var strategyIndex = i;

                group.Go(async ct =>
                {
                    var result = await ExecuteWithPolicyAsync(operation, $"{tier.Name}[{strategyIndex}]", policy, timeProvider, ct).ConfigureAwait(false);
                    if (result.Result.IsSuccess)
                    {
                        result.Compensation.Clear();
                        if (successSource.TrySetResult(result.Result))
                        {
                            await linkedCts.CancelAsync();
                            group.Cancel();
                        }
                    }
                    else
                    {
                        result.Compensation.Clear();
                        var error = result.Result.Error;
                        if (error is null)
                        {
                            errors.Add(DecorateTierError(Error.Unspecified(), tier.Name, tierIndex, strategyIndex));
                        }
                        else if (error.Code == ErrorCodes.Canceled)
                        {
                            if (!cancellationToken.IsCancellationRequested)
                            {
                                return Result.Ok(Go.Unit.Value);
                            }

                            errors.Add(DecorateTierError(error, tier.Name, tierIndex, strategyIndex));
                        }
                        else
                        {
                            errors.Add(DecorateTierError(error, tier.Name, tierIndex, strategyIndex));
                        }
                    }

                    return Result.Ok(Go.Unit.Value);
                });
            }

            var waitTask = group.WaitAsync(cancellationToken);
            var completed = await Task.WhenAny(successSource.Task, waitTask).ConfigureAwait(false);
            if (completed == successSource.Task)
            {
                if (successSource.Task.IsCanceled)
                {
                    return Fail<T>(Error.Canceled(token: cancellationToken));
                }

                try
                {
                    await waitTask.ConfigureAwait(false);
                }
                catch
                {
                    // Ignore cancellations from peers cancelled after success.
                }

                return await successSource.Task.ConfigureAwait(false);
            }

            var waitResult = await waitTask.ConfigureAwait(false);
            if (waitResult is { IsFailure: true, Error: { } groupError })
            {
                if (groupError.Code == ErrorCodes.Canceled && cancellationToken.IsCancellationRequested)
                {
                    return Fail<T>(groupError);
                }

                if (groupError.Code != ErrorCodes.Canceled || !linkedCts.IsCancellationRequested)
                {
                    errors.Add(DecorateTierError(groupError, tier.Name, tierIndex, null));
                }
            }

            if (successSource.Task.IsCompletedSuccessfully)
            {
                return await successSource.Task.ConfigureAwait(false);
            }

            if (!errors.IsEmpty)
            {
                var captured = errors.ToArray();
                var tierError = captured.Length == 1
                    ? captured[0]
                    : Error.Aggregate($"Tier '{tier.Name}' exhausted all strategies.", captured);
                return Fail<T>(tierError);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return Fail<T>(Error.Canceled(token: cancellationToken));
            }

            return Fail<T>(DecorateTierError(Error.From("Tier completed without producing a result.", ErrorCodes.Unspecified), tier.Name, tierIndex, null));
        }
        catch (OperationCanceledException oce)
        {
            return Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    private static Error DecorateTierError(Error error, string tierName, int tierIndex, int? strategyIndex)
    {
        var metadata = new List<KeyValuePair<string, object?>>(strategyIndex.HasValue ? 3 : 2)
        {
            new("fallbackTier", tierName),
            new("tierIndex", tierIndex)
        };

        if (strategyIndex.HasValue)
        {
            metadata.Add(new KeyValuePair<string, object?>("strategyIndex", strategyIndex.Value));
        }

        return error.WithMetadata(metadata);
    }
}
