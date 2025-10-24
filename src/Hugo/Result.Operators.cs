using Hugo.Policies;

namespace Hugo;

public static partial class Result
{
    public static Result<IReadOnlyDictionary<TKey, List<T>>> Group<T, TKey>(IEnumerable<Result<T>> source, Func<T, TKey> keySelector)
        where TKey : notnull
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(keySelector);

        var groups = new Dictionary<TKey, List<T>>();
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

    public static Result<(IReadOnlyList<T> True, IReadOnlyList<T> False)> Partition<T>(IEnumerable<Result<T>> source, Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);

        var trueList = new List<T>();
        var falseList = new List<T>();

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

    public static Result<IReadOnlyList<IReadOnlyList<T>>> Window<T>(IEnumerable<Result<T>> source, int size)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var windows = new List<IReadOnlyList<T>>();
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
                windows.Add(buffer.ToArray());
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            windows.Add(buffer.ToArray());
        }

        return Ok<IReadOnlyList<IReadOnlyList<T>>>(windows);
    }

    /// <summary>
    /// Executes <paramref name="operation"/> under the supplied execution <paramref name="policy"/>, applying retries and compensation as needed.
    /// </summary>
    public static async ValueTask<Result<T>> RetryWithPolicyAsync<T>(Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>> operation, ResultExecutionPolicy policy, CancellationToken cancellationToken = default, TimeProvider? timeProvider = null)
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
