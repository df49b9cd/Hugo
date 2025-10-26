namespace Hugo;

public static partial class Functional
{
    /// <summary>
    /// Executes a side-effect for successful results without altering the pipeline.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tap">The side-effect to execute on success.</param>
    /// <returns>The original result.</returns>
    public static Result<T> Tap<T>(this Result<T> result, Action<T> tap)
    {
        ArgumentNullException.ThrowIfNull(tap);

        if (result.IsSuccess)
        {
            tap(result.Value);
        }

        return result;
    }

    /// <summary>
    /// Alias for <see cref="Tap{T}(Result{T}, Action{T})"/> maintained for backwards compatibility.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tap">The side-effect to execute on success.</param>
    /// <returns>The original result.</returns>
    public static Result<T> Tee<T>(this Result<T> result, Action<T> tap) => result.Tap(tap);

    /// <summary>
    /// Executes an action if the result represents success.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="action">The action executed on success.</param>
    /// <returns>The original result.</returns>
    public static Result<T> OnSuccess<T>(this Result<T> result, Action<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (result.IsSuccess)
        {
            action(result.Value);
        }

        return result;
    }

    /// <summary>
    /// Executes an action if the result represents failure.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="action">The action executed on failure.</param>
    /// <returns>The original result.</returns>
    public static Result<T> OnFailure<T>(this Result<T> result, Action<Error> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (result.IsFailure)
        {
            action(result.Error!);
        }

        return result;
    }

    /// <summary>
    /// Executes a side-effect when the result represents failure.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tap">The side-effect executed on failure.</param>
    /// <returns>The original result.</returns>
    public static Result<T> TapError<T>(this Result<T> result, Action<Error> tap)
    {
        ArgumentNullException.ThrowIfNull(tap);

        if (result.IsFailure)
        {
            tap(result.Error!);
        }

        return result;
    }

    /// <summary>Executes an asynchronous side-effect when the result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tapAsync">The asynchronous side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async Task<Result<T>> TapAsync<T>(
        this Result<T> result,
        Func<T, CancellationToken, Task> tapAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tapAsync);

        if (result.IsFailure)
        {
            return result;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await tapAsync(result.Value, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Executes an asynchronous side-effect when the result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="tap">The side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async Task<Result<T>> TapAsync<T>(
        this Task<Result<T>> resultTask,
        Action<T> tap,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(tap);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            if (result.IsSuccess)
            {
                tap(result.Value);
            }

            return result;
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Executes an asynchronous side-effect when the result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="tapAsync">The asynchronous side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async Task<Result<T>> TapAsync<T>(
        this Task<Result<T>> resultTask,
        Func<T, CancellationToken, Task> tapAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(tapAsync);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            if (result.IsSuccess)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await tapAsync(result.Value, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Alias for <see cref="TapAsync(Result{T}, Func{T, CancellationToken, Task}, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tapAsync">The asynchronous side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static Task<Result<T>> TeeAsync<T>(this Result<T> result, Func<T, CancellationToken, Task> tapAsync, CancellationToken cancellationToken = default) =>
        result.TapAsync(tapAsync, cancellationToken);

    /// <summary>Alias for <see cref="TapAsync(Task{Result{T}}, Func{T, CancellationToken, Task}, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="tapAsync">The asynchronous side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static Task<Result<T>> TeeAsync<T>(this Task<Result<T>> resultTask, Func<T, CancellationToken, Task> tapAsync, CancellationToken cancellationToken = default) =>
        resultTask.TapAsync(tapAsync, cancellationToken);

    /// <summary>Alias for <see cref="TapAsync(Task{Result{T}}, Action{T}, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="tap">The side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static Task<Result<T>> TeeAsync<T>(this Task<Result<T>> resultTask, Action<T> tap, CancellationToken cancellationToken = default) =>
        resultTask.TapAsync(tap, cancellationToken);

    /// <summary>Executes an asynchronous side-effect when the result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tapAsync">The asynchronous side-effect executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async Task<Result<T>> TapErrorAsync<T>(
        this Result<T> result,
        Func<Error, CancellationToken, Task> tapAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(tapAsync);

        if (result.IsSuccess)
        {
            return result;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await tapAsync(result.Error!, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Executes a side-effect when the asynchronous result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="tap">The side-effect executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async Task<Result<T>> TapErrorAsync<T>(
        this Task<Result<T>> resultTask,
        Action<Error> tap,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(tap);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            if (result.IsFailure)
            {
                tap(result.Error!);
            }

            return result;
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Executes an asynchronous side-effect when the asynchronous result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="tapAsync">The asynchronous side-effect executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async Task<Result<T>> TapErrorAsync<T>(
        this Task<Result<T>> resultTask,
        Func<Error, CancellationToken, Task> tapAsync,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(tapAsync);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            if (result.IsFailure)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await tapAsync(result.Error!, cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>Executes an asynchronous action when the result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="action">The asynchronous action executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static Task<Result<T>> OnSuccessAsync<T>(
        this Result<T> result,
        Func<T, CancellationToken, Task> action,
        CancellationToken cancellationToken = default
    ) => result.TapAsync(action, cancellationToken);

    /// <summary>Executes a synchronous action when the asynchronous result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="action">The action executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static Task<Result<T>> OnSuccessAsync<T>(
        this Task<Result<T>> resultTask,
        Action<T> action,
        CancellationToken cancellationToken = default
    ) => resultTask.TapAsync(action, cancellationToken);

    /// <summary>Executes an asynchronous action when the asynchronous result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="action">The asynchronous action executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static Task<Result<T>> OnSuccessAsync<T>(
        this Task<Result<T>> resultTask,
        Func<T, CancellationToken, Task> action,
        CancellationToken cancellationToken = default
    ) => resultTask.TapAsync(action, cancellationToken);

    /// <summary>Executes an asynchronous action when the result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="action">The asynchronous action executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static Task<Result<T>> OnFailureAsync<T>(
        this Result<T> result,
        Func<Error, CancellationToken, Task> action,
        CancellationToken cancellationToken = default
    ) => result.TapErrorAsync(action, cancellationToken);

    /// <summary>Executes a synchronous action when the asynchronous result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="action">The action executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static Task<Result<T>> OnFailureAsync<T>(
        this Task<Result<T>> resultTask,
        Action<Error> action,
        CancellationToken cancellationToken = default
    ) => resultTask.TapErrorAsync(action, cancellationToken);

    /// <summary>Executes an asynchronous action when the asynchronous result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="action">The asynchronous action executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static Task<Result<T>> OnFailureAsync<T>(
        this Task<Result<T>> resultTask,
        Func<Error, CancellationToken, Task> action,
        CancellationToken cancellationToken = default
    ) => resultTask.TapErrorAsync(action, cancellationToken);
}
