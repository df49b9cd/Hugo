namespace Hugo;

public static partial class Result
{
    /// <summary>
    /// Aggregates a sequence of results into a single result containing all successful values.
    /// </summary>
    /// <typeparam name="T">The type of the aggregated values.</typeparam>
    /// <param name="results">The results to aggregate.</param>
    /// <returns>A successful result containing all values or the first failure encountered.</returns>
    public static Result<IReadOnlyList<T>> Sequence<T>(IEnumerable<Result<T>> results)
    {
        ArgumentNullException.ThrowIfNull(results);

        var values = new List<T>();
        foreach (var result in results)
        {
            if (result.IsFailure)
            {
                return Fail<IReadOnlyList<T>>(result.Error!);
            }

            values.Add(result.Value);
        }

        return Ok<IReadOnlyList<T>>(values);
    }

    /// <summary>
    /// Applies a selector to each value in the source and aggregates the successful results.
    /// </summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source items to transform.</param>
    /// <param name="selector">The selector applied to each item.</param>
    /// <returns>A successful result containing all projected values or the first failure encountered.</returns>
    public static Result<IReadOnlyList<TOut>> Traverse<TIn, TOut>(IEnumerable<TIn> source, Func<TIn, Result<TOut>> selector)
    {
        ArgumentNullException.ThrowIfNull(source);

        ArgumentNullException.ThrowIfNull(selector);

        var values = new List<TOut>();
        foreach (var item in source)
        {
            var result = selector(item);
            if (result.IsFailure)
            {
                return Fail<IReadOnlyList<TOut>>(result.Error!);
            }

            values.Add(result.Value);
        }

        return Ok<IReadOnlyList<TOut>>(values);
    }

    /// <summary>
    /// Applies an asynchronous selector to each value in the source and aggregates the successful results.
    /// </summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source items to transform.</param>
    /// <param name="selector">The selector applied to each item.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <summary>
    /// Applies an asynchronous selector to each value in the source and aggregates the successful results without incurring additional <see cref="Task"/> allocations.
    /// </summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source items to transform.</param>
    /// <param name="selector">The selector applied to each item.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> that resolves to a successful result containing all projected values or the first failure encountered.</returns>
    /// <remarks>
    /// The returned <see cref="ValueTask{TResult}"/> must be awaited exactly once. If you need to await multiple times, cache the awaited result or wrap the <see cref="ValueTask{TResult}"/> in a <see cref="Task"/> manually.
    /// </remarks>
    public static ValueTask<Result<IReadOnlyList<TOut>>> TraverseAsync<TIn, TOut>(IEnumerable<TIn> source, Func<TIn, ValueTask<Result<TOut>>> selector, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);

        return TraverseAsync(source, (item, _) => selector(item), cancellationToken);
    }

    /// <summary>
    /// Applies an asynchronous selector that returns <see cref="ValueTask{TResult}"/> values to each item in the source and aggregates the successful results.
    /// </summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source items to transform.</param>
    /// <param name="selector">The selector applied to each item.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> that resolves to a successful result containing all projected values or the first failure encountered.</returns>
    /// <remarks>
    /// The returned <see cref="ValueTask{TResult}"/> must be awaited exactly once; call <see cref="ValueTask{TResult}.AsTask"/> if you need to integrate with APIs that expect a <see cref="Task"/>.
    /// </remarks>
    public static async ValueTask<Result<IReadOnlyList<TOut>>> TraverseAsync<TIn, TOut>(
        IEnumerable<TIn> source,
        Func<TIn, CancellationToken, ValueTask<Result<TOut>>> selector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        ArgumentNullException.ThrowIfNull(selector);

        var values = new List<TOut>();

        try
        {
            foreach (var item in source)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await selector(item, cancellationToken).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return Fail<IReadOnlyList<TOut>>(result.Error!);
                }

                values.Add(result.Value);
            }

            return Ok<IReadOnlyList<TOut>>(values);
        }
        catch (OperationCanceledException oce)
        {
            return Fail<IReadOnlyList<TOut>>(Error.Canceled(token: oce.CancellationToken));
        }
    }

    /// <summary>
    /// Aggregates an asynchronous sequence of results into a single result containing all successful values.
    /// </summary>
    /// <typeparam name="T">The type of the aggregated values.</typeparam>
    /// <param name="results">The results to aggregate.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that resolves to a successful result containing all values or the first failure encountered.</returns>
    public static async ValueTask<Result<IReadOnlyList<T>>> SequenceAsync<T>(IAsyncEnumerable<Result<T>> results, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(results);

        var values = new List<T>();
        try
        {
            await foreach (var result in results.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                if (result.IsFailure)
                {
                    return Fail<IReadOnlyList<T>>(result.Error!);
                }

                values.Add(result.Value);
            }

            return Ok<IReadOnlyList<T>>(values);
        }
        catch (OperationCanceledException oce)
        {
            return Fail<IReadOnlyList<T>>(Error.Canceled(token: oce.CancellationToken));
        }
        catch (Exception ex)
        {
            return Fail<IReadOnlyList<T>>(Error.FromException(ex));
        }
    }

    /// <summary>Applies an asynchronous selector to each value in the source and aggregates the successful results.</summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source items to transform.</param>
    /// <param name="selector">The selector applied to each item.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> that resolves to a successful result containing all projected values or the first failure encountered.</returns>
    /// <summary>Applies an asynchronous selector to each value in the source and aggregates the successful results.</summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source items to transform.</param>
    /// <param name="selector">The selector applied to each item.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A <see cref="ValueTask{TResult}"/> that resolves to a successful result containing all projected values or the first failure encountered.</returns>
    /// <remarks>
    /// The returned <see cref="ValueTask{TResult}"/> must be awaited exactly once; use <see cref="ValueTask{TResult}.AsTask"/> when interoperability with <see cref="Task"/>-based callers is required.
    /// </remarks>
    public static async ValueTask<Result<IReadOnlyList<TOut>>> TraverseAsync<TIn, TOut>(
        IAsyncEnumerable<TIn> source,
        Func<TIn, CancellationToken, ValueTask<Result<TOut>>> selector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        ArgumentNullException.ThrowIfNull(selector);

        var values = new List<TOut>();

        try
        {
            await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                var result = await selector(item, cancellationToken).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    return Fail<IReadOnlyList<TOut>>(result.Error!);
                }

                values.Add(result.Value);
            }

            return Ok<IReadOnlyList<TOut>>(values);
        }
        catch (OperationCanceledException oce)
        {
            return Fail<IReadOnlyList<TOut>>(Error.Canceled(token: oce.CancellationToken));
        }
        catch (Exception ex)
        {
            return Fail<IReadOnlyList<TOut>>(Error.FromException(ex));
        }
    }
}
