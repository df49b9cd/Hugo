namespace Hugo;

public static partial class Functional
{
    /// <summary>
    /// Ensures that a successful result satisfies the provided predicate.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="predicate">The predicate evaluated against the successful value.</param>
    /// <param name="errorFactory">An optional factory that produces an error when the predicate fails.</param>
    /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
    public static Result<T> Ensure<T>(this Result<T> result, Func<T, bool> predicate, Func<T, Error>? errorFactory = null)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (result.IsFailure || predicate(result.Value))
        {
            return result;
        }

        var error = errorFactory?.Invoke(result.Value) ?? Error.From("The result did not satisfy the required condition.", ErrorCodes.Validation);
        return Result.Fail<T>(error);
    }

    /// <summary>
    /// Filters results in LINQ queries. Equivalent to <see cref="Ensure{T}(Result{T}, Func{T, bool}, Func{T, Error}?)"/>.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="predicate">The predicate evaluated against the successful value.</param>
    /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
    public static Result<T> Where<T>(this Result<T> result, Func<T, bool> predicate) =>
        result.Ensure(predicate);

    /// <summary>Ensures that a successful asynchronous result satisfies the provided predicate.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="predicate">The asynchronous predicate evaluated on success.</param>
    /// <param name="cancellationToken">The token used to cancel predicate evaluation.</param>
    /// <param name="errorFactory">An optional factory that produces an error when the predicate fails.</param>
    /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
    public static async Task<Result<T>> EnsureAsync<T>(
        this Result<T> result,
        Func<T, CancellationToken, Task<bool>> predicate,
        CancellationToken cancellationToken = default,
        Func<T, Error>? errorFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (result.IsFailure)
        {
            return result;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var satisfied = await predicate(result.Value, cancellationToken).ConfigureAwait(false);
            return satisfied ? result : Result.Fail<T>(errorFactory?.Invoke(result.Value) ?? Error.From("The result did not satisfy the required condition.", ErrorCodes.Validation));
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Ensures that a successful asynchronous result satisfies the provided predicate.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="predicate">The asynchronous predicate evaluated on success.</param>
    /// <param name="cancellationToken">The token used to cancel predicate evaluation.</param>
    /// <param name="errorFactory">An optional factory that produces an error when the predicate fails.</param>
    /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
    public static async Task<Result<T>> EnsureAsync<T>(
        this Task<Result<T>> resultTask,
        Func<T, CancellationToken, Task<bool>> predicate,
        CancellationToken cancellationToken = default,
        Func<T, Error>? errorFactory = null
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(predicate);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            return await result.EnsureAsync(predicate, cancellationToken, errorFactory).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }
}
