namespace Hugo;

public static partial class Functional
{
    /// <summary>
    /// Transforms the successful value of the result while preserving failures.
    /// </summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The mapped value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="mapper">The mapper executed for successful results.</param>
    /// <returns>A result containing the mapped value or the original failure.</returns>
    public static Result<TOut> Map<TIn, TOut>(this Result<TIn> result, Func<TIn, TOut> mapper) => mapper is null
            ? throw new ArgumentNullException(nameof(mapper))
            : result.IsFailure ? Result.Fail<TOut>(result.Error!) : Result.Ok(mapper(result.Value));

    /// <summary>
    /// Maps a result inside a LINQ query.
    /// </summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The mapped value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="selector">The mapper executed on success.</param>
    /// <returns>A result containing the mapped value or the original failure.</returns>
    public static Result<TOut> Select<TIn, TOut>(this Result<TIn> result, Func<TIn, TOut> selector) =>
        result.Map(selector);

    /// <summary>Maps the value of an asynchronous result using a synchronous mapping function.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The mapped value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="mapper">The mapper executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the mapping.</param>
    /// <returns>A result containing the mapped value or the original failure.</returns>
    public static async Task<Result<TOut>> MapAsync<TIn, TOut>(
        this Task<Result<TIn>> resultTask,
        Func<TIn, TOut> mapper,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(mapper);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            if (result.IsFailure)
            {
                return Result.Fail<TOut>(result.Error!);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return Result.Ok(mapper(result.Value));
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Maps the value of an asynchronous result using an asynchronous mapping function.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The mapped value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="mapper">The mapper executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the mapping.</param>
    /// <returns>A result containing the mapped value or the original failure.</returns>
    public static async Task<Result<TOut>> MapAsync<TIn, TOut>(
        this Task<Result<TIn>> resultTask,
        Func<TIn, CancellationToken, Task<TOut>> mapper,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(mapper);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            if (result.IsFailure)
            {
                return Result.Fail<TOut>(result.Error!);
            }

            cancellationToken.ThrowIfCancellationRequested();
            var value = await mapper(result.Value, cancellationToken).ConfigureAwait(false);
            return Result.Ok(value);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Maps the value of a result using an asynchronous mapping function.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The mapped value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="mapper">The mapper executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the mapping.</param>
    /// <returns>A result containing the mapped value or the original failure.</returns>
    public static async Task<Result<TOut>> MapAsync<TIn, TOut>(
        this Result<TIn> result,
        Func<TIn, CancellationToken, Task<TOut>> mapper,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(mapper);

        if (result.IsFailure)
        {
            return Result.Fail<TOut>(result.Error!);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var value = await mapper(result.Value, cancellationToken).ConfigureAwait(false);
            return Result.Ok(value);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
        }
    }
}
