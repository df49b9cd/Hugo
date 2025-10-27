namespace Hugo;

/// <summary>
/// Provides functional-style extension methods for composing <see cref="Result{T}"/> values using
/// "railway oriented" patterns.
/// </summary>
public static class Functional
{
    // -----------------
    // Synchronous APIs
    // -----------------

    /// <summary>Chains an operation that executes only when the source result is successful.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The type of the chained result value.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="next">The operation to execute when the source is successful.</param>
    /// <returns>A result produced by <paramref name="next"/> or the original failure.</returns>
    public static Result<TOut> Then<TIn, TOut>(this Result<TIn> result, Func<TIn, Result<TOut>> next) => next is null
            ? throw new ArgumentNullException(nameof(next))
            : result.IsFailure ? Result.Fail<TOut>(result.Error!) : next(result.Value);

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

    /// <summary>
    /// Recovers from failure by executing the provided recovery function.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="recover">The recovery function executed when the result represents failure.</param>
    /// <returns>The original result when successful; otherwise the recovery result.</returns>
    public static Result<T> Recover<T>(this Result<T> result, Func<Error, Result<T>> recover) => recover is null ? throw new ArgumentNullException(nameof(recover)) : result.IsFailure ? recover(result.Error!) : result;

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

    /// <summary>
    /// Filters results in LINQ queries. Equivalent to <see cref="Ensure{T}(Result{T}, Func{T, bool}, Func{T, Error}?)"/>.
    /// </summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="predicate">The predicate evaluated against the successful value.</param>
    /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
    public static Result<T> Where<T>(this Result<T> result, Func<T, bool> predicate) =>
        result.Ensure(predicate);

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

    // -----------------
    // Asynchronous APIs
    // -----------------

    /// <summary>Chains an asynchronous operation onto a synchronous result.</summary>
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

    /// <summary>Ensures that a successful asynchronous result satisfies the provided predicate.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="predicate">The asynchronous predicate evaluated on success.</param>
    /// <param name="errorFactory">An optional factory that produces an error when the predicate fails.</param>
    /// <param name="cancellationToken">The token used to cancel predicate evaluation.</param>
    /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
    public static async Task<Result<T>> EnsureAsync<T>(
        this Result<T> result,
        Func<T, CancellationToken, Task<bool>> predicate,
        Func<T, Error>? errorFactory = null,
        CancellationToken cancellationToken = default
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
        Func<T, Error>? errorFactory = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(resultTask);

        ArgumentNullException.ThrowIfNull(predicate);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await resultTask.ConfigureAwait(false);
            return await result.EnsureAsync(predicate, errorFactory, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
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
