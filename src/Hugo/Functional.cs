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

    /// <param name="result">The source result.</param>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    extension<TIn>(Result<TIn> result)
    {
        /// <summary>Chains an operation that executes only when the source result is successful.</summary>
        /// <typeparam name="TOut">The type of the chained result value.</typeparam>
        /// <param name="next">The operation to execute when the source is successful.</param>
        /// <returns>A result produced by <paramref name="next"/> or the original failure.</returns>
        public Result<TOut> Then<TOut>(Func<TIn, Result<TOut>> next) => next is null
            ? throw new ArgumentNullException(nameof(next))
            : result.IsFailure ? result.CastFailure<TOut>() : next(result.Value);

        /// <summary>
        /// Transforms the successful value of the result while preserving failures.
        /// </summary>
        /// <typeparam name="TOut">The mapped value type.</typeparam>
        /// <param name="mapper">The mapper executed for successful results.</param>
        /// <returns>A result containing the mapped value or the original failure.</returns>
        public Result<TOut> Map<TOut>(Func<TIn, TOut> mapper) => mapper is null
            ? throw new ArgumentNullException(nameof(mapper))
            : result.IsFailure ? result.CastFailure<TOut>() : Result.Ok(mapper(result.Value));

        /// <summary>
        /// Executes a side-effect for successful results without altering the pipeline.
        /// </summary>
        /// <param name="tap">The side-effect to execute on success.</param>
        /// <returns>The original result.</returns>
        public Result<TIn> Tap(Action<TIn> tap)
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
        /// <param name="tap">The side-effect to execute on success.</param>
        /// <returns>The original result.</returns>
        public Result<TIn> Tee(Action<TIn> tap) => result.Tap(tap);

        /// <summary>
        /// Runs the appropriate function depending on whether the result succeeded.
        /// </summary>
        /// <typeparam name="TOut">The projected value type.</typeparam>
        /// <param name="onSuccess">Invoked when the result represents success.</param>
        /// <param name="onError">Invoked when the result represents failure.</param>
        /// <returns>The value produced by <paramref name="onSuccess"/> or <paramref name="onError"/>.</returns>
        public TOut Finally<TOut>(Func<TIn, TOut> onSuccess, Func<Error, TOut> onError)
        {
            ArgumentNullException.ThrowIfNull(onSuccess);

            return onError is null
                ? throw new ArgumentNullException(nameof(onError))
                : result.IsSuccess ? onSuccess(result.Value) : onError(result.Error!);
        }

        /// <summary>
        /// Recovers from failure by executing the provided recovery function.
        /// </summary>
        /// <param name="recover">The recovery function executed when the result represents failure.</param>
        /// <returns>The original result when successful; otherwise the recovery result.</returns>
        public Result<TIn> Recover(Func<Error, Result<TIn>> recover) => recover is null ? throw new ArgumentNullException(nameof(recover)) : result.IsFailure ? recover(result.Error!) : result;

        /// <summary>
        /// Ensures that a successful result satisfies the provided predicate.
        /// </summary>
        /// <param name="predicate">The predicate evaluated against the successful value.</param>
        /// <param name="errorFactory">An optional factory that produces an error when the predicate fails.</param>
        /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
        public Result<TIn> Ensure(Func<TIn, bool> predicate, Func<TIn, Error>? errorFactory = null)
        {
            ArgumentNullException.ThrowIfNull(predicate);

            if (result.IsFailure || predicate(result.Value))
            {
                return result;
            }

            var error = errorFactory?.Invoke(result.Value) ?? Error.From("The result did not satisfy the required condition.", ErrorCodes.Validation);
            return Result.Fail<TIn>(error);
        }

        /// <summary>
        /// Executes an action if the result represents success.
        /// </summary>
        /// <param name="action">The action executed on success.</param>
        /// <returns>The original result.</returns>
        public Result<TIn> OnSuccess(Action<TIn> action)
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
        /// <param name="action">The action executed on failure.</param>
        /// <returns>The original result.</returns>
        public Result<TIn> OnFailure(Action<Error> action)
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
        /// <param name="tap">The side-effect executed on failure.</param>
        /// <returns>The original result.</returns>
        public Result<TIn> TapError(Action<Error> tap)
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
        /// <param name="predicate">The predicate evaluated against the successful value.</param>
        /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
        public Result<TIn> Where(Func<TIn, bool> predicate) =>
            result.Ensure(predicate);

        /// <summary>
        /// Maps a result inside a LINQ query.
        /// </summary>
        /// <typeparam name="TOut">The mapped value type.</typeparam>
        /// <param name="selector">The mapper executed on success.</param>
        /// <returns>A result containing the mapped value or the original failure.</returns>
        public Result<TOut> Select<TOut>(Func<TIn, TOut> selector) =>
            result.Map(selector);

        /// <summary>
        /// Supports LINQ query comprehension with <c>from</c>/<c>select</c>.
        /// </summary>
        /// <typeparam name="TMiddle">The intermediate value type.</typeparam>
        /// <typeparam name="TOut">The projected value type.</typeparam>
        /// <param name="binder">The binder invoked on success.</param>
        /// <param name="projector">The projector that combines source and intermediate values.</param>
        /// <returns>A result containing the projected value or the first failure encountered.</returns>
        public Result<TOut> SelectMany<TMiddle, TOut>(Func<TIn, Result<TMiddle>> binder,
            Func<TIn, TMiddle, TOut> projector
        )
        {
            ArgumentNullException.ThrowIfNull(binder);

            ArgumentNullException.ThrowIfNull(projector);

            if (result.IsFailure)
            {
                return result.CastFailure<TOut>();
            }

            var intermediate = binder(result.Value);
            return intermediate.IsFailure ? Result.Fail<TOut>(intermediate.Error!) : Result.Ok(projector(result.Value, intermediate.Value));
        }

        /// <summary>Chains an asynchronous operation onto a synchronous result.</summary>
        /// <typeparam name="TOut">The type of the chained result value.</typeparam>
        /// <param name="next">The asynchronous operation executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the chained operation.</param>
        /// <returns>A result produced by <paramref name="next"/> or the original failure.</returns>
        public async ValueTask<Result<TOut>> ThenAsync<TOut>(Func<TIn, CancellationToken, ValueTask<Result<TOut>>> next,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(next);

            if (result.IsFailure)
            {
                return result.CastFailure<TOut>();
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
    }

    // -----------------
    // Asynchronous APIs
    // -----------------

    /// <param name="resultTask">The source result task.</param>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    extension<TIn>(Task<Result<TIn>> resultTask)
    {
        /// <summary>Chains a synchronous operation onto an asynchronous result.</summary>
        /// <typeparam name="TOut">The type of the chained result value.</typeparam>
        /// <param name="next">The operation executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the chained operation.</param>
        /// <returns>A result produced by <paramref name="next"/> or the original failure.</returns>
        public async ValueTask<Result<TOut>> ThenAsync<TOut>(Func<TIn, Result<TOut>> next,
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
                    return result.CastFailure<TOut>();
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
        /// <typeparam name="TOut">The type of the chained result value.</typeparam>
        /// <param name="next">The asynchronous operation executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the chained operation.</param>
        /// <returns>A result produced by <paramref name="next"/> or the original failure.</returns>
        public async ValueTask<Result<TOut>> ThenAsync<TOut>(Func<TIn, CancellationToken, ValueTask<Result<TOut>>> next,
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
                    return result.CastFailure<TOut>();
                }

                cancellationToken.ThrowIfCancellationRequested();
                return await next(result.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
            }
        }
    }

    /// <summary>Chains a ValueTask-based operation onto a synchronous result.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The type of the chained result value.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="next">The ValueTask-producing operation executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the chained operation.</param>
    /// <returns>A result produced by <paramref name="next"/> or the original failure.</returns>
    public static async ValueTask<Result<TOut>> ThenValueTaskAsync<TIn, TOut>(
        this Result<TIn> result,
        Func<TIn, CancellationToken, ValueTask<Result<TOut>>> next,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(next);

        if (result.IsFailure)
        {
            return result.CastFailure<TOut>();
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

    /// <summary>Chains a ValueTask-based operation onto an asynchronous result.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The type of the chained result value.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="next">The ValueTask-producing operation executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the chained operation.</param>
    /// <returns>A result produced by <paramref name="next"/> or the original failure.</returns>
    public static async ValueTask<Result<TOut>> ThenValueTaskAsync<TIn, TOut>(
        this Task<Result<TIn>> resultTask,
        Func<TIn, CancellationToken, ValueTask<Result<TOut>>> next,
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
                return result.CastFailure<TOut>();
            }

            cancellationToken.ThrowIfCancellationRequested();
            return await next(result.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <param name="resultTask">The ValueTask that produces the source result.</param>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    extension<TIn>(ValueTask<Result<TIn>> resultTask)
    {
        /// <summary>Chains a synchronous operation onto a ValueTask-based result.</summary>
        /// <typeparam name="TOut">The type of the chained result value.</typeparam>
        /// <param name="next">The operation executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the chained operation.</param>
        /// <returns>A ValueTask containing a result produced by <paramref name="next"/> or the original failure.</returns>
        public async ValueTask<Result<TOut>> ThenAsync<TOut>(Func<TIn, Result<TOut>> next,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(next);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await resultTask.ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result.CastFailure<TOut>();
                }

                cancellationToken.ThrowIfCancellationRequested();
                return next(result.Value);
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
            }
        }

        /// <summary>Chains an asynchronous operation onto a ValueTask-based result.</summary>
        /// <typeparam name="TOut">The type of the chained result value.</typeparam>
        /// <param name="next">The asynchronous operation executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the chained operation.</param>
        /// <returns>A ValueTask containing a result produced by <paramref name="next"/> or the original failure.</returns>
        public async ValueTask<Result<TOut>> ThenAsync<TOut>(Func<TIn, CancellationToken, ValueTask<Result<TOut>>> next,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(next);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await resultTask.ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result.CastFailure<TOut>();
                }

                cancellationToken.ThrowIfCancellationRequested();
                return await next(result.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
            }
        }

        /// <summary>Chains a ValueTask-based operation onto a ValueTask-based result.</summary>
        /// <typeparam name="TOut">The type of the chained result value.</typeparam>
        /// <param name="next">The ValueTask-producing operation executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the chained operation.</param>
        /// <returns>A ValueTask containing a result produced by <paramref name="next"/> or the original failure.</returns>
        public async ValueTask<Result<TOut>> ThenValueTaskAsync<TOut>(Func<TIn, CancellationToken, ValueTask<Result<TOut>>> next,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(next);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await resultTask.ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result.CastFailure<TOut>();
                }

                cancellationToken.ThrowIfCancellationRequested();
                return await next(result.Value, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
            }
        }
    }

    /// <param name="resultTask">The source result task.</param>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    extension<TIn>(Task<Result<TIn>> resultTask)
    {
        /// <summary>Maps the value of an asynchronous result using a synchronous mapping function.</summary>
        /// <typeparam name="TOut">The mapped value type.</typeparam>
        /// <param name="mapper">The mapper executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the mapping.</param>
        /// <returns>A result containing the mapped value or the original failure.</returns>
        public async ValueTask<Result<TOut>> MapAsync<TOut>(Func<TIn, TOut> mapper,
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
                    return result.CastFailure<TOut>();
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
        /// <typeparam name="TOut">The mapped value type.</typeparam>
        /// <param name="mapper">The mapper executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the mapping.</param>
        /// <returns>A result containing the mapped value or the original failure.</returns>
        public async ValueTask<Result<TOut>> MapAsync<TOut>(Func<TIn, CancellationToken, ValueTask<TOut>> mapper,
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
                    return result.CastFailure<TOut>();
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

        /// <summary>Maps the value of an asynchronous result using a ValueTask-based mapping function.</summary>
        /// <typeparam name="TOut">The mapped value type.</typeparam>
        /// <param name="mapper">The ValueTask mapper executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the mapping.</param>
        /// <returns>A result containing the mapped value or the original failure.</returns>
        public async ValueTask<Result<TOut>> MapValueTaskAsync<TOut>(Func<TIn, CancellationToken, ValueTask<TOut>> mapper,
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
                    return result.CastFailure<TOut>();
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
    }

    /// <param name="result">The source result.</param>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    extension<TIn>(Result<TIn> result)
    {
        /// <summary>Maps the value of a result using an asynchronous mapping function.</summary>
        /// <typeparam name="TOut">The mapped value type.</typeparam>
        /// <param name="mapper">The mapper executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the mapping.</param>
        /// <returns>A result containing the mapped value or the original failure.</returns>
        public async ValueTask<Result<TOut>> MapAsync<TOut>(Func<TIn, CancellationToken, ValueTask<TOut>> mapper,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(mapper);

            if (result.IsFailure)
            {
                return result.CastFailure<TOut>();
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

        /// <summary>Maps the value of a result using a ValueTask-based mapping function.</summary>
        /// <typeparam name="TOut">The mapped value type.</typeparam>
        /// <param name="mapper">The ValueTask mapper executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the mapping.</param>
        /// <returns>A result containing the mapped value or the original failure.</returns>
        public async ValueTask<Result<TOut>> MapValueTaskAsync<TOut>(Func<TIn, CancellationToken, ValueTask<TOut>> mapper,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(mapper);

            if (result.IsFailure)
            {
                return result.CastFailure<TOut>();
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

    /// <param name="resultTask">The ValueTask that produces the source result.</param>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    extension<TIn>(ValueTask<Result<TIn>> resultTask)
    {
        /// <summary>Maps the value of a ValueTask-based result using a synchronous mapping function.</summary>
        /// <typeparam name="TOut">The mapped value type.</typeparam>
        /// <param name="mapper">The mapper executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the mapping.</param>
        /// <returns>A ValueTask containing the mapped result or the original failure.</returns>
        public async ValueTask<Result<TOut>> MapAsync<TOut>(Func<TIn, TOut> mapper,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(mapper);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await resultTask.ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result.CastFailure<TOut>();
                }

                cancellationToken.ThrowIfCancellationRequested();
                return Result.Ok(mapper(result.Value));
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
            }
        }

        /// <summary>Maps the value of a ValueTask-based result using a Task-based mapping function.</summary>
        /// <typeparam name="TOut">The mapped value type.</typeparam>
        /// <param name="mapper">The asynchronous mapper executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the mapping.</param>
        /// <returns>A ValueTask containing the mapped result or the original failure.</returns>
        public async ValueTask<Result<TOut>> MapAsync<TOut>(Func<TIn, CancellationToken, ValueTask<TOut>> mapper,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(mapper);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await resultTask.ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result.CastFailure<TOut>();
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

        /// <summary>Maps the value of a ValueTask-based result using a ValueTask-based mapping function.</summary>
        /// <typeparam name="TOut">The mapped value type.</typeparam>
        /// <param name="mapper">The ValueTask mapper executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the mapping.</param>
        /// <returns>A ValueTask containing the mapped result or the original failure.</returns>
        public async ValueTask<Result<TOut>> MapValueTaskAsync<TOut>(Func<TIn, CancellationToken, ValueTask<TOut>> mapper,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(mapper);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await resultTask.ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result.CastFailure<TOut>();
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
    }

    /// <summary>Executes an asynchronous side-effect when the result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tapAsync">The asynchronous side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async ValueTask<Result<T>> TapAsync<T>(
        this Result<T> result,
        Func<T, CancellationToken, ValueTask> tapAsync,
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

    /// <param name="resultTask">The source result task.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(Task<Result<T>> resultTask)
    {
        /// <summary>Executes an asynchronous side-effect when the result succeeds.</summary>
        /// <param name="tap">The side-effect executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public async ValueTask<Result<T>> TapAsync(Action<T> tap,
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
        /// <param name="tapAsync">The asynchronous side-effect executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public async ValueTask<Result<T>> TapAsync(Func<T, CancellationToken, ValueTask> tapAsync,
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
    }

    /// <summary>Executes a ValueTask-based side-effect when the result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tapAsync">The ValueTask side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async ValueTask<Result<T>> TapValueTaskAsync<T>(
        this Result<T> result,
        Func<T, CancellationToken, ValueTask> tapAsync,
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

    /// <summary>Executes a ValueTask-based side-effect when the asynchronous result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="tapAsync">The ValueTask side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async ValueTask<Result<T>> TapValueTaskAsync<T>(
        this Task<Result<T>> resultTask,
        Func<T, CancellationToken, ValueTask> tapAsync,
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

    /// <param name="resultTask">The ValueTask that produces the source result.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(ValueTask<Result<T>> resultTask)
    {
        /// <summary>Executes a side-effect when the ValueTask-based result succeeds.</summary>
        /// <param name="tap">The side-effect executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public async ValueTask<Result<T>> TapAsync(Action<T> tap,
            CancellationToken cancellationToken = default
        )
        {
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

        /// <summary>Executes a Task-based side-effect when the ValueTask-based result succeeds.</summary>
        /// <param name="tapAsync">The asynchronous side-effect executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public async ValueTask<Result<T>> TapAsync(Func<T, CancellationToken, ValueTask> tapAsync,
            CancellationToken cancellationToken = default
        )
        {
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

        /// <summary>Executes a ValueTask-based side-effect when the ValueTask-based result succeeds.</summary>
        /// <param name="tapAsync">The ValueTask side-effect executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public async ValueTask<Result<T>> TapValueTaskAsync(Func<T, CancellationToken, ValueTask> tapAsync,
            CancellationToken cancellationToken = default
        )
        {
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
    }

    /// <summary>Alias for <see cref="TapAsync(Result{T}, Func{T, CancellationToken, Task}, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tapAsync">The asynchronous side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static ValueTask<Result<T>> TeeAsync<T>(this Result<T> result, Func<T, CancellationToken, ValueTask> tapAsync, CancellationToken cancellationToken = default) =>
        result.TapAsync(tapAsync, cancellationToken);

    /// <param name="resultTask">The source result task.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(Task<Result<T>> resultTask)
    {
        /// <summary>Alias for <see cref="TapAsync(Task{Result{T}}, Func{T, CancellationToken, Task}, CancellationToken)"/>.</summary>
        /// <param name="tapAsync">The asynchronous side-effect executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public ValueTask<Result<T>> TeeAsync(Func<T, CancellationToken, ValueTask> tapAsync, CancellationToken cancellationToken = default) =>
            resultTask.TapAsync(tapAsync, cancellationToken);

        /// <summary>Alias for <see cref="TapAsync(Task{Result{T}}, Action{T}, CancellationToken)"/>.</summary>
        /// <param name="tap">The side-effect executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public ValueTask<Result<T>> TeeAsync(Action<T> tap, CancellationToken cancellationToken = default) =>
            resultTask.TapAsync(tap, cancellationToken);
    }

    /// <summary>Alias for <see cref="TapAsync(Result{T}, Func{T, CancellationToken, ValueTask}, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tapAsync">The ValueTask side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static ValueTask<Result<T>> TeeValueTaskAsync<T>(this Result<T> result, Func<T, CancellationToken, ValueTask> tapAsync, CancellationToken cancellationToken = default) =>
        result.TapValueTaskAsync(tapAsync, cancellationToken);

    /// <summary>Alias for <see cref="TapAsync(Task{Result{T}}, Func{T, CancellationToken, ValueTask}, CancellationToken)"/>.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="tapAsync">The ValueTask side-effect executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static ValueTask<Result<T>> TeeValueTaskAsync<T>(this Task<Result<T>> resultTask, Func<T, CancellationToken, ValueTask> tapAsync, CancellationToken cancellationToken = default) =>
        resultTask.TapValueTaskAsync(tapAsync, cancellationToken);

    /// <param name="resultTask">The ValueTask that produces the source result.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(ValueTask<Result<T>> resultTask)
    {
        /// <summary>Alias for <see cref="TapAsync(ValueTask{Result{T}}, Func{T, CancellationToken, Task}, CancellationToken)"/>.</summary>
        /// <param name="tapAsync">The Task-based side-effect executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public ValueTask<Result<T>> TeeAsync(Func<T, CancellationToken, ValueTask> tapAsync, CancellationToken cancellationToken = default) =>
            resultTask.TapAsync(tapAsync, cancellationToken);

        /// <summary>Alias for <see cref="TapAsync(ValueTask{Result{T}}, Func{T, CancellationToken, ValueTask}, CancellationToken)"/>.</summary>
        /// <param name="tapAsync">The ValueTask side-effect executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public ValueTask<Result<T>> TeeValueTaskAsync(Func<T, CancellationToken, ValueTask> tapAsync, CancellationToken cancellationToken = default) =>
            resultTask.TapValueTaskAsync(tapAsync, cancellationToken);

        /// <summary>Alias for <see cref="TapAsync(ValueTask{Result{T}}, Action{T}, CancellationToken)"/>.</summary>
        /// <param name="tap">The side-effect executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public ValueTask<Result<T>> TeeAsync(Action<T> tap, CancellationToken cancellationToken = default) =>
            resultTask.TapAsync(tap, cancellationToken);
    }

    /// <summary>Executes an asynchronous side-effect when the result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tapAsync">The asynchronous side-effect executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async ValueTask<Result<T>> TapErrorAsync<T>(
        this Result<T> result,
        Func<Error, CancellationToken, ValueTask> tapAsync,
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

    /// <param name="resultTask">The source result task.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(Task<Result<T>> resultTask)
    {
        /// <summary>Executes a side-effect when the asynchronous result represents failure.</summary>
        /// <param name="tap">The side-effect executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public async ValueTask<Result<T>> TapErrorAsync(Action<Error> tap,
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
        /// <param name="tapAsync">The asynchronous side-effect executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public async ValueTask<Result<T>> TapErrorAsync(Func<Error, CancellationToken, ValueTask> tapAsync,
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
    }

    /// <summary>Executes a ValueTask-based side-effect when the result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="tapAsync">The ValueTask side-effect executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async ValueTask<Result<T>> TapErrorValueTaskAsync<T>(
        this Result<T> result,
        Func<Error, CancellationToken, ValueTask> tapAsync,
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

    /// <summary>Executes a ValueTask-based side-effect when the asynchronous result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="tapAsync">The ValueTask side-effect executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
    /// <returns>The original result or a failure if the side-effect was canceled.</returns>
    public static async ValueTask<Result<T>> TapErrorValueTaskAsync<T>(
        this Task<Result<T>> resultTask,
        Func<Error, CancellationToken, ValueTask> tapAsync,
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

    /// <param name="resultTask">The ValueTask that produces the source result.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(ValueTask<Result<T>> resultTask)
    {
        /// <summary>Executes a side-effect when the ValueTask-based result represents failure.</summary>
        /// <param name="tap">The side-effect executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public async ValueTask<Result<T>> TapErrorAsync(Action<Error> tap,
            CancellationToken cancellationToken = default
        )
        {
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

        /// <summary>Executes a Task-based side-effect when the ValueTask-based result represents failure.</summary>
        /// <param name="tapAsync">The asynchronous side-effect executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public async ValueTask<Result<T>> TapErrorAsync(Func<Error, CancellationToken, ValueTask> tapAsync,
            CancellationToken cancellationToken = default
        )
        {
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

        /// <summary>Executes a ValueTask-based side-effect when the ValueTask-based result represents failure.</summary>
        /// <param name="tapAsync">The ValueTask side-effect executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the side-effect.</param>
        /// <returns>The original result or a failure if the side-effect was canceled.</returns>
        public async ValueTask<Result<T>> TapErrorValueTaskAsync(Func<Error, CancellationToken, ValueTask> tapAsync,
            CancellationToken cancellationToken = default
        )
        {
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
    }

    /// <summary>Executes an asynchronous action when the result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="action">The asynchronous action executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static ValueTask<Result<T>> OnSuccessAsync<T>(
        this Result<T> result,
        Func<T, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default
    ) => result.TapAsync(action, cancellationToken);

    /// <param name="resultTask">The source result task.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(Task<Result<T>> resultTask)
    {
        /// <summary>Executes a synchronous action when the asynchronous result succeeds.</summary>
        /// <param name="action">The action executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the action.</param>
        /// <returns>The original result or a failure if the action was canceled.</returns>
        public ValueTask<Result<T>> OnSuccessAsync(Action<T> action,
            CancellationToken cancellationToken = default
        ) => resultTask.TapAsync(action, cancellationToken);

        /// <summary>Executes an asynchronous action when the asynchronous result succeeds.</summary>
        /// <param name="action">The asynchronous action executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the action.</param>
        /// <returns>The original result or a failure if the action was canceled.</returns>
        public ValueTask<Result<T>> OnSuccessAsync(Func<T, CancellationToken, ValueTask> action,
            CancellationToken cancellationToken = default
        ) => resultTask.TapAsync(action, cancellationToken);
    }

    /// <summary>Executes a ValueTask-based action when the result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="action">The ValueTask action executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static ValueTask<Result<T>> OnSuccessValueTaskAsync<T>(
        this Result<T> result,
        Func<T, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default
    ) => result.TapValueTaskAsync(action, cancellationToken);

    /// <summary>Executes a ValueTask-based action when the asynchronous result succeeds.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="action">The ValueTask action executed on success.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static ValueTask<Result<T>> OnSuccessValueTaskAsync<T>(
        this Task<Result<T>> resultTask,
        Func<T, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default
    ) => resultTask.TapValueTaskAsync(action, cancellationToken);

    /// <param name="resultTask">The ValueTask that produces the source result.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(ValueTask<Result<T>> resultTask)
    {
        /// <summary>Executes a synchronous action when the ValueTask-based result succeeds.</summary>
        /// <param name="action">The action executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the action.</param>
        /// <returns>The original result or a failure if the action was canceled.</returns>
        public ValueTask<Result<T>> OnSuccessAsync(Action<T> action,
            CancellationToken cancellationToken = default
        ) => resultTask.TapAsync(action, cancellationToken);

        /// <summary>Executes a Task-based action when the ValueTask-based result succeeds.</summary>
        /// <param name="action">The asynchronous action executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the action.</param>
        /// <returns>The original result or a failure if the action was canceled.</returns>
        public ValueTask<Result<T>> OnSuccessAsync(Func<T, CancellationToken, ValueTask> action,
            CancellationToken cancellationToken = default
        ) => resultTask.TapAsync(action, cancellationToken);

        /// <summary>Executes a ValueTask-based action when the ValueTask-based result succeeds.</summary>
        /// <param name="action">The ValueTask action executed on success.</param>
        /// <param name="cancellationToken">The token used to cancel the action.</param>
        /// <returns>The original result or a failure if the action was canceled.</returns>
        public ValueTask<Result<T>> OnSuccessValueTaskAsync(Func<T, CancellationToken, ValueTask> action,
            CancellationToken cancellationToken = default
        ) => resultTask.TapValueTaskAsync(action, cancellationToken);
    }

    /// <summary>Executes an asynchronous action when the result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="action">The asynchronous action executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static ValueTask<Result<T>> OnFailureAsync<T>(
        this Result<T> result,
        Func<Error, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default
    ) => result.TapErrorAsync(action, cancellationToken);

    /// <param name="resultTask">The source result task.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(Task<Result<T>> resultTask)
    {
        /// <summary>Executes a synchronous action when the asynchronous result represents failure.</summary>
        /// <param name="action">The action executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the action.</param>
        /// <returns>The original result or a failure if the action was canceled.</returns>
        public ValueTask<Result<T>> OnFailureAsync(Action<Error> action,
            CancellationToken cancellationToken = default
        ) => resultTask.TapErrorAsync(action, cancellationToken);

        /// <summary>Executes an asynchronous action when the asynchronous result represents failure.</summary>
        /// <param name="action">The asynchronous action executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the action.</param>
        /// <returns>The original result or a failure if the action was canceled.</returns>
        public ValueTask<Result<T>> OnFailureAsync(Func<Error, CancellationToken, ValueTask> action,
            CancellationToken cancellationToken = default
        ) => resultTask.TapErrorAsync(action, cancellationToken);
    }

    /// <summary>Executes a ValueTask-based action when the result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="result">The source result.</param>
    /// <param name="action">The ValueTask action executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static ValueTask<Result<T>> OnFailureValueTaskAsync<T>(
        this Result<T> result,
        Func<Error, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default
    ) => result.TapErrorValueTaskAsync(action, cancellationToken);

    /// <summary>Executes a ValueTask-based action when the asynchronous result represents failure.</summary>
    /// <typeparam name="T">The result value type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="action">The ValueTask action executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the action.</param>
    /// <returns>The original result or a failure if the action was canceled.</returns>
    public static ValueTask<Result<T>> OnFailureValueTaskAsync<T>(
        this Task<Result<T>> resultTask,
        Func<Error, CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default
    ) => resultTask.TapErrorValueTaskAsync(action, cancellationToken);

    /// <param name="resultTask">The ValueTask that produces the source result.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(ValueTask<Result<T>> resultTask)
    {
        /// <summary>Executes a synchronous action when the ValueTask-based result represents failure.</summary>
        /// <param name="action">The action executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the action.</param>
        /// <returns>The original result or a failure if the action was canceled.</returns>
        public ValueTask<Result<T>> OnFailureAsync(Action<Error> action,
            CancellationToken cancellationToken = default
        ) => resultTask.TapErrorAsync(action, cancellationToken);

        /// <summary>Executes a Task-based action when the ValueTask-based result represents failure.</summary>
        /// <param name="action">The asynchronous action executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the action.</param>
        /// <returns>The original result or a failure if the action was canceled.</returns>
        public ValueTask<Result<T>> OnFailureAsync(Func<Error, CancellationToken, ValueTask> action,
            CancellationToken cancellationToken = default
        ) => resultTask.TapErrorAsync(action, cancellationToken);

        /// <summary>Executes a ValueTask-based action when the ValueTask-based result represents failure.</summary>
        /// <param name="action">The ValueTask action executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the action.</param>
        /// <returns>The original result or a failure if the action was canceled.</returns>
        public ValueTask<Result<T>> OnFailureValueTaskAsync(Func<Error, CancellationToken, ValueTask> action,
            CancellationToken cancellationToken = default
        ) => resultTask.TapErrorValueTaskAsync(action, cancellationToken);
    }

    /// <param name="result">The source result.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(Result<T> result)
    {
        /// <summary>Recovers from a failed result with an asynchronous recovery function.</summary>
        /// <param name="recoverAsync">The recovery function executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the recovery.</param>
        /// <returns>A result that is either the original success or the recovery outcome.</returns>
        public async ValueTask<Result<T>> RecoverAsync(Func<Error, CancellationToken, ValueTask<Result<T>>> recoverAsync,
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

        /// <summary>Recovers from a failed result with a ValueTask-based recovery function.</summary>
        /// <param name="recoverAsync">The recovery function executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the recovery.</param>
        /// <returns>A result that is either the original success or the recovery outcome.</returns>
        public async ValueTask<Result<T>> RecoverValueTaskAsync(Func<Error, CancellationToken, ValueTask<Result<T>>> recoverAsync,
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
    }

    /// <param name="resultTask">The source result task.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(Task<Result<T>> resultTask)
    {
        /// <summary>Recovers from a failed asynchronous result with a synchronous recovery function.</summary>
        /// <param name="recover">The recovery function executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the recovery.</param>
        /// <returns>A result that is either the original success or the recovery outcome.</returns>
        public async ValueTask<Result<T>> RecoverAsync(Func<Error, Result<T>> recover,
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
        /// <param name="recoverAsync">The recovery function executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the recovery.</param>
        /// <returns>A result that is either the original success or the recovery outcome.</returns>
        public async ValueTask<Result<T>> RecoverAsync(Func<Error, CancellationToken, ValueTask<Result<T>>> recoverAsync,
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

        /// <summary>Recovers from a failed asynchronous result with a ValueTask-based recovery function.</summary>
        /// <param name="recoverAsync">The recovery function executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the recovery.</param>
        /// <returns>A result that is either the original success or the recovery outcome.</returns>
        public async ValueTask<Result<T>> RecoverValueTaskAsync(Func<Error, CancellationToken, ValueTask<Result<T>>> recoverAsync,
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

    /// <param name="resultTask">The ValueTask that produces the source result.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(ValueTask<Result<T>> resultTask)
    {
        /// <summary>Recovers from a failed ValueTask-based result with a synchronous recovery function.</summary>
        /// <param name="recover">The recovery function executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the recovery.</param>
        /// <returns>A result that is either the original success or the recovery outcome.</returns>
        public async ValueTask<Result<T>> RecoverAsync(Func<Error, Result<T>> recover,
            CancellationToken cancellationToken = default
        )
        {
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

        /// <summary>Recovers from a failed ValueTask-based result with a Task-based recovery function.</summary>
        /// <param name="recoverAsync">The recovery function executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the recovery.</param>
        /// <returns>A result that is either the original success or the recovery outcome.</returns>
        public async ValueTask<Result<T>> RecoverAsync(Func<Error, CancellationToken, ValueTask<Result<T>>> recoverAsync,
            CancellationToken cancellationToken = default
        )
        {
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

        /// <summary>Recovers from a failed ValueTask-based result with a ValueTask-based recovery function.</summary>
        /// <param name="recoverAsync">The recovery function executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the recovery.</param>
        /// <returns>A result that is either the original success or the recovery outcome.</returns>
        public async ValueTask<Result<T>> RecoverValueTaskAsync(Func<Error, CancellationToken, ValueTask<Result<T>>> recoverAsync,
            CancellationToken cancellationToken = default
        )
        {
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

    /// <param name="result">The source result.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(Result<T> result)
    {
        /// <summary>Ensures that a successful asynchronous result satisfies the provided predicate.</summary>
        /// <param name="predicate">The asynchronous predicate evaluated on success.</param>
        /// <param name="errorFactory">An optional factory that produces an error when the predicate fails.</param>
        /// <param name="cancellationToken">The token used to cancel predicate evaluation.</param>
        /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
        public async ValueTask<Result<T>> EnsureAsync(Func<T, CancellationToken, ValueTask<bool>> predicate,
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

        /// <summary>Ensures that a successful result satisfies the provided ValueTask-based predicate.</summary>
        /// <param name="predicate">The ValueTask predicate evaluated on success.</param>
        /// <param name="errorFactory">An optional factory that produces an error when the predicate fails.</param>
        /// <param name="cancellationToken">The token used to cancel predicate evaluation.</param>
        /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
        public async ValueTask<Result<T>> EnsureValueTaskAsync(Func<T, CancellationToken, ValueTask<bool>> predicate,
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
    }

    /// <param name="resultTask">The source result task.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(Task<Result<T>> resultTask)
    {
        /// <summary>Ensures that a successful asynchronous result satisfies the provided predicate.</summary>
        /// <param name="predicate">The asynchronous predicate evaluated on success.</param>
        /// <param name="cancellationToken">The token used to cancel predicate evaluation.</param>
        /// <param name="errorFactory">An optional factory that produces an error when the predicate fails.</param>
        /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
        public async ValueTask<Result<T>> EnsureAsync(Func<T, CancellationToken, ValueTask<bool>> predicate,
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

        /// <summary>Ensures that a successful asynchronous result satisfies the provided ValueTask-based predicate.</summary>
        /// <param name="predicate">The ValueTask predicate evaluated on success.</param>
        /// <param name="errorFactory">An optional factory that produces an error when the predicate fails.</param>
        /// <param name="cancellationToken">The token used to cancel predicate evaluation.</param>
        /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
        public async ValueTask<Result<T>> EnsureValueTaskAsync(Func<T, CancellationToken, ValueTask<bool>> predicate,
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
                return await result.EnsureValueTaskAsync(predicate, errorFactory, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
            }
        }
    }

    /// <param name="resultTask">The ValueTask that produces the source result.</param>
    /// <typeparam name="T">The result value type.</typeparam>
    extension<T>(ValueTask<Result<T>> resultTask)
    {
        /// <summary>Ensures that a successful ValueTask-based result satisfies the provided predicate.</summary>
        /// <param name="predicate">The Task-based predicate evaluated on success.</param>
        /// <param name="errorFactory">An optional factory that produces an error when the predicate fails.</param>
        /// <param name="cancellationToken">The token used to cancel predicate evaluation.</param>
        /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
        public async ValueTask<Result<T>> EnsureAsync(Func<T, CancellationToken, ValueTask<bool>> predicate,
            Func<T, Error>? errorFactory = null,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(predicate);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await resultTask.ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var satisfied = await predicate(result.Value, cancellationToken).ConfigureAwait(false);
                return satisfied ? result : Result.Fail<T>(errorFactory?.Invoke(result.Value) ?? Error.From("The result did not satisfy the required condition.", ErrorCodes.Validation));
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
            }
        }

        /// <summary>Ensures that a successful ValueTask-based result satisfies the provided ValueTask-based predicate.</summary>
        /// <param name="predicate">The ValueTask predicate evaluated on success.</param>
        /// <param name="errorFactory">An optional factory that produces an error when the predicate fails.</param>
        /// <param name="cancellationToken">The token used to cancel predicate evaluation.</param>
        /// <returns>The original result when the predicate succeeds; otherwise a failure.</returns>
        public async ValueTask<Result<T>> EnsureValueTaskAsync(Func<T, CancellationToken, ValueTask<bool>> predicate,
            Func<T, Error>? errorFactory = null,
            CancellationToken cancellationToken = default
        )
        {
            ArgumentNullException.ThrowIfNull(predicate);

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await resultTask.ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return result;
                }

                cancellationToken.ThrowIfCancellationRequested();
                var satisfied = await predicate(result.Value, cancellationToken).ConfigureAwait(false);
                return satisfied ? result : Result.Fail<T>(errorFactory?.Invoke(result.Value) ?? Error.From("The result did not satisfy the required condition.", ErrorCodes.Validation));
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<T>(Error.Canceled(token: oce.CancellationToken));
            }
        }
    }

    /// <param name="resultTask">The source result task.</param>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    extension<TIn>(Task<Result<TIn>> resultTask)
    {
        /// <summary>Matches a result to one of two asynchronous continuations.</summary>
        /// <typeparam name="TOut">The projection type.</typeparam>
        /// <param name="onSuccess">The continuation executed on success.</param>
        /// <param name="onError">The continuation executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the continuations.</param>
        /// <returns>The value produced by the invoked continuation.</returns>
        public async ValueTask<TOut> FinallyAsync<TOut>(Func<TIn, TOut> onSuccess,
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

        /// <summary>Matches a result to ValueTask-based continuations.</summary>
        /// <typeparam name="TOut">The projection type.</typeparam>
        /// <param name="onSuccess">The ValueTask continuation executed on success.</param>
        /// <param name="onError">The ValueTask continuation executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the continuations.</param>
        /// <returns>The value produced by the invoked continuation.</returns>
        public async ValueTask<TOut> FinallyValueTaskAsync<TOut>(Func<TIn, CancellationToken, ValueTask<TOut>> onSuccess,
            Func<Error, CancellationToken, ValueTask<TOut>> onError,
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

    /// <param name="result">The source result.</param>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    extension<TIn>(Result<TIn> result)
    {
        /// <summary>Matches a result to asynchronous continuations.</summary>
        /// <typeparam name="TOut">The projection type.</typeparam>
        /// <param name="onSuccess">The continuation executed on success.</param>
        /// <param name="onError">The continuation executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the continuations.</param>
        /// <returns>The value produced by the invoked continuation.</returns>
        public async ValueTask<TOut> FinallyAsync<TOut>(Func<TIn, CancellationToken, ValueTask<TOut>> onSuccess,
            Func<Error, CancellationToken, ValueTask<TOut>> onError,
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

        /// <summary>Matches a result to ValueTask-based asynchronous continuations.</summary>
        /// <typeparam name="TOut">The projection type.</typeparam>
        /// <param name="onSuccess">The ValueTask continuation executed on success.</param>
        /// <param name="onError">The ValueTask continuation executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the continuations.</param>
        /// <returns>The value produced by the invoked continuation.</returns>
        public async ValueTask<TOut> FinallyValueTaskAsync<TOut>(Func<TIn, CancellationToken, ValueTask<TOut>> onSuccess,
            Func<Error, CancellationToken, ValueTask<TOut>> onError,
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
    }

    /// <summary>Executes asynchronous continuations on a task result.</summary>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    /// <typeparam name="TOut">The projection type.</typeparam>
    /// <param name="resultTask">The source result task.</param>
    /// <param name="onSuccess">The continuation executed on success.</param>
    /// <param name="onError">The continuation executed on failure.</param>
    /// <param name="cancellationToken">The token used to cancel the continuations.</param>
    /// <returns>The value produced by the invoked continuation.</returns>
    public static async ValueTask<TOut> FinallyAsync<TIn, TOut>(
        this Task<Result<TIn>> resultTask,
        Func<TIn, CancellationToken, ValueTask<TOut>> onSuccess,
        Func<Error, CancellationToken, ValueTask<TOut>> onError,
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

    /// <param name="resultTask">The ValueTask that produces the source result.</param>
    /// <typeparam name="TIn">The type of the source result value.</typeparam>
    extension<TIn>(ValueTask<Result<TIn>> resultTask)
    {
        /// <summary>Matches a ValueTask-based result to synchronous continuations.</summary>
        /// <typeparam name="TOut">The projection type.</typeparam>
        /// <param name="onSuccess">The continuation executed on success.</param>
        /// <param name="onError">The continuation executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the continuations.</param>
        /// <returns>The value produced by the invoked continuation.</returns>
        public async ValueTask<TOut> FinallyAsync<TOut>(Func<TIn, TOut> onSuccess,
            Func<Error, TOut> onError,
            CancellationToken cancellationToken = default
        )
        {
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

        /// <summary>Matches a ValueTask-based result to Task-based continuations.</summary>
        /// <typeparam name="TOut">The projection type.</typeparam>
        /// <param name="onSuccess">The Task continuation executed on success.</param>
        /// <param name="onError">The Task continuation executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the continuations.</param>
        /// <returns>The value produced by the invoked continuation.</returns>
        public async ValueTask<TOut> FinallyAsync<TOut>(Func<TIn, CancellationToken, ValueTask<TOut>> onSuccess,
            Func<Error, CancellationToken, ValueTask<TOut>> onError,
            CancellationToken cancellationToken = default
        )
        {
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

        /// <summary>Matches a ValueTask-based result to ValueTask-based continuations.</summary>
        /// <typeparam name="TOut">The projection type.</typeparam>
        /// <param name="onSuccess">The ValueTask continuation executed on success.</param>
        /// <param name="onError">The ValueTask continuation executed on failure.</param>
        /// <param name="cancellationToken">The token used to cancel the continuations.</param>
        /// <returns>The value produced by the invoked continuation.</returns>
        public async ValueTask<TOut> FinallyValueTaskAsync<TOut>(Func<TIn, CancellationToken, ValueTask<TOut>> onSuccess,
            Func<Error, CancellationToken, ValueTask<TOut>> onError,
            CancellationToken cancellationToken = default
        )
        {
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
}
