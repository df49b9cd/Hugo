using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;

using Hugo.Policies;
using Unit = Hugo.Go.Unit;

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

        var list = operations as IList<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> ?? [.. operations];
        if (list.Count == 0)
        {
            throw new ArgumentException("At least one fallback operation must be provided.", nameof(operations));
        }

        Name = name;
        _operations = [.. list];
    }

    /// <summary>Gets the name assigned to the fallback tier.</summary>
    public string Name { get; }

    internal IReadOnlyList<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> Operations => _operations;

    /// <summary>Creates a tier from operations that accept a cancellation token.</summary>
    /// <param name="name">The name assigned to the fallback tier.</param>
    /// <param name="operations">The operations evaluated within the tier.</param>
    /// <returns>A configured fallback tier.</returns>
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The factory simplifies tier construction while retaining generic type safety.")]
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
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The factory simplifies tier construction while retaining generic type safety.")]
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
    /// <param name="timeProvider">The optional time provider used for policy timing.</param>
    /// <param name="cancellationToken">The token used to cancel the fallback execution.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> describing the outcome of the fallback orchestration.</returns>
    public static ValueTask<Result<T>> TieredFallbackAsync<T>(IEnumerable<ResultFallbackTier<T>> tiers,
        ResultExecutionPolicy? policy = null,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default) => TieredFallbackInternal(tiers, policy, timeProvider ?? TimeProvider.System, cancellationToken);

    private static async ValueTask<Result<T>> TieredFallbackInternal<T>(IEnumerable<ResultFallbackTier<T>> tiers,
        ResultExecutionPolicy? policy,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tiers);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var tierList = tiers as IList<ResultFallbackTier<T>> ?? [.. tiers];
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

        var aggregate = Error.Aggregate("Fallback pipeline exhausted all tiers.", [.. tierErrors]);
        return Fail<T>(aggregate);
    }

    private static async ValueTask<Result<T>> ExecuteTierAsync<T>(
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

            var successSource = new ResultCompletionSource<Result<T>>();
            var errors = new ConcurrentBag<Error>();

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            using var group = new ErrGroup(linkedCts.Token);

            for (var i = 0; i < operations.Count; i++)
            {
                var operation = operations[i];
                var strategyIndex = i;

                group.Go(ExecuteStrategyAsync);

                async ValueTask<Result<Unit>> ExecuteStrategyAsync(CancellationToken ct)
                {
                    var result = await ExecuteWithPolicyAsync(operation, $"{tier.Name}[{strategyIndex}]", policy, timeProvider, ct).ConfigureAwait(false);
                    if (result.Result.IsSuccess)
                    {
                        result.Compensation.Clear();
                        if (successSource.TrySetResult(result.Result))
                        {
                            await linkedCts.CancelAsync().ConfigureAwait(false);
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

                    return Result.Ok(Unit.Value);
                }
            }

            var waitValueTask = new ValueTask<Result<Unit>>(group.WaitAsync(cancellationToken));
            var completed = await ValueTaskUtilities.WhenAny(successSource.ValueTask, waitValueTask).ConfigureAwait(false);
            if (completed == 0)
            {
                if (successSource.IsCanceled)
                {
                    return Fail<T>(Error.Canceled(token: cancellationToken));
                }

                try
                {
                    await waitValueTask.ConfigureAwait(false);
                }
                catch
                {
                }

                return await successSource.ValueTask.ConfigureAwait(false);
            }

            var waitResult = await waitValueTask.ConfigureAwait(false);
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

            if (successSource.IsCompletedSuccessfully)
            {
                return await successSource.ValueTask.ConfigureAwait(false);
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
