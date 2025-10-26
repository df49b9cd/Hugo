namespace Hugo;

public static partial class Functional
{
    /// <summary>
    /// Recovers from failure by executing the provided recovery function.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="recover">The recovery function executed when the result represents failure.</param>
    /// <returns>The original result when successful; otherwise the recovery result.</returns>
    public static Result<T> Recover<T>(this Result<T> result, Func<Error, Result<T>> recover) => recover is null ? throw new ArgumentNullException(nameof(recover)) : result.IsFailure ? recover(result.Error!) : result;

    /// <summary>Recovers from a failed result with an asynchronous recovery function.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="recoverAsync">The recovery function executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the recovery.</param>
    /// <returns>A result that is either the original success or the recovery outcome.</returns>
    public static async Task<Result<T>> RecoverAsync<T>(
        this Result<T> result,
        Func<Error, CancellationToken, Task<Result<T>>> recoverAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(recoverAsync);

        if (result.IsSuccess)
        {
            return result;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await recoverAsync(result.Error!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Recovers from a failed asynchronous result with a synchronous recovery function.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="recover">The recovery function executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the recovery.</param>
    /// <returns>A result that is either the original success or the recovery outcome.</returns>
    public static async Task<Result<T>> RecoverAsync<T>(
        this Task<Result<T>> resultTask,
        Func<Error, Result<T>> recover,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(recover);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            return result.IsFailure ? recover(result.Error!) : result;
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Recovers from a failed asynchronous result with an asynchronous recovery function.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="recoverAsync">The recovery function executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the recovery.</param>
    /// <returns>A result that is either the original success or the recovery outcome.</returns>
    public static async Task<Result<T>> RecoverAsync<T>(
        this Task<Result<T>> resultTask,
        Func<Error, CancellationToken, Task<Result<T>>> recoverAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(recoverAsync);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            if (result.IsSuccess)
            {
                return result;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await recoverAsync(result.Error!, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }
}
