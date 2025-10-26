namespace Hugo;

public static partial class Functional
{
    /// <summary>Chains an operation that executes only when the source result is successful.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The type of the chained result value.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="next">The operation to execute when the source is successful.</param>
    /// <returns>A result produced by <paramref name="next"/> or the original failure.</returns>
    public static Result<TOut> Then<TIn, TOut>(this Result<TIn> result, Func<TIn, Result<TOut>> next) => next is null
            ? throw new ArgumentNullException(nameof(next))
            : result.IsFailure ? Result.Fail<TOut>(result.Error!) : next(result.Value);

    /// <summary>Chains an operation that executes only when the source result is successful.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The type of the chained result value.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="next">The asynchronous operation executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the chained operation.</param>
    /// <returns>A result produced by <paramref name="next"/> or the original failure.</returns>
    public static async Task<Result<TOut>> ThenAsync<TIn, TOut>(
        this Result<TIn> result,
        Func<TIn, CancellationToken, Task<Result<TOut>>> next,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(next);

        if (result.IsFailure)
        {
            return Result.Fail<TOut>(result.Error!);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await next(result.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Chains a synchronous operation onto an asynchronous result.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The type of the chained result value.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="next">The operation executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the chained operation.</param>
    /// <returns>A result produced by <paramref name="next"/> or the original failure.</returns>
    public static async Task<Result<TOut>> ThenAsync<TIn, TOut>(
        this Task<Result<TIn>> resultTask,
        Func<TIn, Result<TOut>> next,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(next);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            if (result.IsFailure)
            {
                return Result.Fail<TOut>(result.Error!);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return next(result.Value);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Chains an asynchronous operation onto an asynchronous result.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The type of the chained result value.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="next">The asynchronous operation executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the chained operation.</param>
    /// <returns>A result produced by <paramref name="next"/> or the original failure.</returns>
    public static async Task<Result<TOut>> ThenAsync<TIn, TOut>(
        this Task<Result<TIn>> resultTask,
        Func<TIn, CancellationToken, Task<Result<TOut>>> next,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(next);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            if (result.IsFailure)
            {
                return Result.Fail<TOut>(result.Error!);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await next(result.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>
    /// Supports LINQ query comprehension with <c>from</c>/<c>select</c>.
    /// </summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TMiddle">The intermediate value type.</typeparam>
    /// <typeparam name="TOut">The projected value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="binder">The binder invoked on success.</param>
    /// <param name="projector">The projector that combines source and intermediate values.</param>
    /// <returns>A result containing the projected value or the first failure encountered.</returns>
    public static Result<TOut> SelectMany<TIn, TMiddle, TOut>(
        this Result<TIn> result,
        Func<TIn, Result<TMiddle>> binder,
        Func<TIn, TMiddle, TOut> projector
    )
    {
        ArgumentNullException.ThrowIfNull(binder);

        ArgumentNullException.ThrowIfNull(projector);

        if (result.IsFailure)
        {
            return Result.Fail<TOut>(result.Error!);
        }

        var intermediate = binder(result.Value);
        return intermediate.IsFailure ? Result.Fail<TOut>(intermediate.Error!) : Result.Ok(projector(result.Value, intermediate.Value));
    }
}
