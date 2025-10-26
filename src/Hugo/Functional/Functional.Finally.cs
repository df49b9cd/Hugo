namespace Hugo;

public static partial class Functional
{
    /// <summary>
    /// Runs the appropriate function depending on whether the result succeeded.
    /// </summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The projected value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="onSuccess">Invoked when the result represents success.</param>
    /// <param name="onError">Invoked when the result represents failure.</param>
    /// <returns>The value produced by <paramref name="onSuccess"/> or <paramref name="onError"/>.</returns>
    public static TOut Finally<TIn, TOut>(this Result<TIn> result, Func<TIn, TOut> onSuccess, Func<Error, TOut> onError)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        return onError is null
            ? throw new ArgumentNullException(nameof(onError))
            : result.IsSuccess ? onSuccess(result.Value) : onError(result.Error!);
    }

    /// <summary>Matches a result to one of two asynchronous continuations.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The projection type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="onSuccess">The continuation executed on success.</param>
    /// <param name="onError">The continuation executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the continuations.</param>
    /// <returns>The value produced by the invoked continuation.</returns>
    public static async Task<TOut> FinallyAsync<TIn, TOut>(
        this Task<Result<TIn>> resultTask,
        Func<TIn, TOut> onSuccess,
        Func<Error, TOut> onError,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(onSuccess);

        ArgumentNullException.ThrowIfNull(onError);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result.IsSuccess ? onSuccess(result.Value) : onError(result.Error!);
        }
        catch (OperationCanceledException oce)
        {
            return onError(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Matches a result to asynchronous continuations.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The projection type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="onSuccess">The continuation executed on success.</param>
    /// <param name="onError">The continuation executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the continuations.</param>
    /// <returns>The value produced by the invoked continuation.</returns>
    public static async Task<TOut> FinallyAsync<TIn, TOut>(
        this Result<TIn> result,
        Func<TIn, CancellationToken, Task<TOut>> onSuccess,
        Func<Error, CancellationToken, Task<TOut>> onError,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        ArgumentNullException.ThrowIfNull(onError);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return result.IsSuccess
                ? await onSuccess(result.Value, cancellationToken).ConfigureAwait(false)
                : await onError(result.Error!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            return await onError(Error.Canceled(token: oce.CancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Executes asynchronous continuations on a task result.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The projection type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="onSuccess">The continuation executed on success.</param>
    /// <param name="onError">The continuation executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the continuations.</param>
    /// <returns>The value produced by the invoked continuation.</returns>
    public static async Task<TOut> FinallyAsync<TIn, TOut>(
        this Task<Result<TIn>> resultTask,
        Func<TIn, CancellationToken, Task<TOut>> onSuccess,
        Func<Error, CancellationToken, Task<TOut>> onError,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(onSuccess);

        ArgumentNullException.ThrowIfNull(onError);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result.IsSuccess
                ? await onSuccess(result.Value, cancellationToken).ConfigureAwait(false)
                : await onError(result.Error!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            return await onError(Error.Canceled(token: oce.CancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }
}
