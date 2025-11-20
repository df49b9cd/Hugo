using Hugo.Policies;

namespace Hugo;

public static partial class Result
{
    /// <summary>Groups successful results by a key while propagating failures.</summary>
    /// <typeparam name="T">The type of the result values.</typeparam>
    /// <typeparam name="TKey">The type of the grouping key.</typeparam>
    /// <param name="source">The result sequence to group.</param>
    /// <param name="keySelector">The delegate that projects keys from successful values.</param>
    /// <returns>A successful result containing grouped values or the first failure encountered.</returns>
    public static Result<IReadOnlyDictionary<TKey, List<T>>> Group<T, TKey>(IEnumerable<Result<T>> source, Func<T, TKey> keySelector)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keySelector);

        var expectedCount = TryGetCount(source);
        var groups = expectedCount > 0 ? new Dictionary<TKey, List<T>>(expectedCount) : new Dictionary<TKey, List<T>>();
        foreach (var result in source)
        {
            if (result.IsFailure)
            {
                return Fail<IReadOnlyDictionary<TKey, List<T>>>(result.Error);
            }

            var key = keySelector(result.Value);
            if (!groups.TryGetValue(key, out var list))
            {
                list = [];
                groups[key] = list;
            }

            list.Add(result.Value);
        }

        return Ok<IReadOnlyDictionary<TKey, List<T>>>(groups);
    }

    /// <summary>Splits successful results into two partitions while propagating failures.</summary>
    /// <typeparam name="T">The type of the result values.</typeparam>
    /// <param name="source">The result sequence to partition.</param>
    /// <param name="predicate">The predicate that decides the target partition.</param>
    /// <returns>A successful result containing both partitions or the first failure encountered.</returns>
    public static Result<(IReadOnlyList<T> True, IReadOnlyList<T> False)> Partition<T>(IEnumerable<Result<T>> source, Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);

        var expectedCount = TryGetCount(source);
        var trueList = expectedCount > 0 ? new List<T>(expectedCount) : new List<T>();
        var falseList = expectedCount > 0 ? new List<T>(expectedCount) : new List<T>();

        foreach (var result in source)
        {
            if (result.IsFailure)
            {
                return Fail<(IReadOnlyList<T>, IReadOnlyList<T>)>(result.Error);
            }

            if (predicate(result.Value))
            {
                trueList.Add(result.Value);
            }
            else
            {
                falseList.Add(result.Value);
            }
        }

        return Ok<(IReadOnlyList<T>, IReadOnlyList<T>)>((trueList, falseList));
    }

    /// <summary>Splits successful results into fixed-size windows while propagating failures.</summary>
    /// <typeparam name="T">The type of the result values.</typeparam>
    /// <param name="source">The result sequence to window.</param>
    /// <param name="size">The number of items per window.</param>
    /// <returns>A successful result containing the windows or the first failure encountered.</returns>
    public static Result<IReadOnlyList<IReadOnlyList<T>>> Window<T>(IEnumerable<Result<T>> source, int size)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var sourceCount = TryGetCount(source);
        var windowCapacity = sourceCount > 0 ? (int)Math.Ceiling(sourceCount / (double)size) : 0;
        var windows = windowCapacity > 0 ? new List<IReadOnlyList<T>>(windowCapacity) : new List<IReadOnlyList<T>>();
        var buffer = new List<T>(size);

        foreach (var result in source)
        {
            if (result.IsFailure)
            {
                return Fail<IReadOnlyList<IReadOnlyList<T>>>(result.Error);
            }

            buffer.Add(result.Value);
            if (buffer.Count == size)
            {
                windows.Add([.. buffer]);
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            windows.Add([.. buffer]);
        }

        return Ok<IReadOnlyList<IReadOnlyList<T>>>(windows);
    }

    /// <summary>
    /// Executes <paramref name="operation"/> under the supplied execution <paramref name="policy"/>, applying retries and compensation as needed.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="operation">The operation to execute under policy control.</param>
    /// <param name="policy">The execution policy that governs retries and compensation.</param>
    /// <param name="timeProvider">The optional time provider used for policy timing.</param>
    /// <param name="cancellationToken">The token used to cancel the execution.</param>
    /// <returns>A result describing the policy-governed execution.</returns>
    public static async ValueTask<Result<T>> RetryWithPolicyAsync<T>(Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>> operation, ResultExecutionPolicy policy, TimeProvider? timeProvider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(policy);

        var provider = timeProvider ?? TimeProvider.System;
        var scope = new CompensationScope();
        var result = await ExecuteWithPolicyAsync(operation, "retry", policy, provider, cancellationToken).ConfigureAwait(false);
        if (result.Result.IsSuccess)
        {
            scope.Absorb(result.Compensation);
            scope.Clear();
            return result.Result;
        }

        scope.Absorb(result.Compensation);
        result.Compensation.Clear();
        var compensationError = await RunCompensationAsync(policy, scope, cancellationToken).ConfigureAwait(false);
        if (compensationError is not null)
        {
            var failure = Error.Aggregate("Retry operation failed with compensation issues.", result.Result.Error ?? Error.Unspecified(), compensationError);
            return Fail<T>(failure);
        }

        return result.Result;
    }
}
