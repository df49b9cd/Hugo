using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Hugo;

/// <summary>
/// Provides static helpers for creating and composing <see cref="Result{T}"/> instances.
/// </summary>
public static partial class Result
{
    /// <summary>Wraps the provided value in a successful result.</summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="value">The value to encapsulate.</param>
    /// <returns>A successful result containing <paramref name="value"/>.</returns>
    public static Result<T> Ok<T>(T value) => Result<T>.Success(value);

    /// <summary>Wraps the provided error in a failed result.</summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="error">The error associated with the failure.</param>
    /// <returns>A failed result containing <paramref name="error"/>.</returns>
    public static Result<T> Fail<T>(Error? error) => Result<T>.Failure(error ?? Error.Unspecified());

    /// <summary>
    /// Converts an optional value into a <see cref="Result{T}"/> using the provided error factory when no value is present.
    /// </summary>
    /// <typeparam name="T">The type of the optional value.</typeparam>
    /// <param name="optional">The optional value to inspect.</param>
    /// <param name="errorFactory">The factory invoked when the optional is empty.</param>
    /// <returns>A successful result containing the optional value or a failure produced by <paramref name="errorFactory"/>.</returns>
    public static Result<T> FromOptional<T>(Optional<T> optional, Func<Error> errorFactory)
    {
        ArgumentNullException.ThrowIfNull(errorFactory);

        return optional.TryGetValue(out var value)
            ? Ok(value)
            : Fail<T>(errorFactory() ?? Error.Unspecified());
    }

    /// <summary>
    /// Executes a synchronous operation and captures any thrown exceptions as <see cref="Error"/> values.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="operation">The operation to execute.</param>
    /// <param name="errorFactory">An optional factory that converts exceptions into errors.</param>
    /// <returns>A successful result containing the operation output or a failure wrapping the thrown exception.</returns>
    public static Result<T> Try<T>(Func<T> operation, Func<Exception, Error?>? errorFactory = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            return Ok(operation());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var error = errorFactory?.Invoke(ex) ?? Error.FromException(ex);
            return Fail<T>(error);
        }
    }

    /// <summary>
    /// Executes an asynchronous operation and captures any thrown exceptions as <see cref="Error"/> values.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="operation">The asynchronous operation to execute.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <param name="errorFactory">An optional factory that converts exceptions into errors.</param>
    /// <returns>A successful result containing the operation output or a failure wrapping the thrown exception.</returns>
    public static async Task<Result<T>> TryAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken cancellationToken = default, Func<Exception, Error?>? errorFactory = null)
    {
        ArgumentNullException.ThrowIfNull(operation);

        try
        {
            var value = await operation(cancellationToken).ConfigureAwait(false);
            return Ok(value);
        }
        catch (OperationCanceledException oce)
        {
            return Fail<T>(Error.Canceled(token: oce.CancellationToken));
        }
        catch (Exception ex)
        {
            var error = errorFactory?.Invoke(ex) ?? Error.FromException(ex);
            return Fail<T>(error);
        }
    }

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
    /// <returns>A task that resolves to a successful result containing all projected values or the first failure encountered.</returns>
    public static Task<Result<IReadOnlyList<TOut>>> TraverseAsync<TIn, TOut>(IEnumerable<TIn> source, Func<TIn, Task<Result<TOut>>> selector, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selector);

        return TraverseAsync(source, (item, _) => selector(item), cancellationToken);
    }

    /// <summary>
    /// Applies an asynchronous selector to each value in the source and aggregates the successful results.
    /// </summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source items to transform.</param>
    /// <param name="selector">The selector applied to each item.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that resolves to a successful result containing all projected values or the first failure encountered.</returns>
    public static async Task<Result<IReadOnlyList<TOut>>> TraverseAsync<TIn, TOut>(
        IEnumerable<TIn> source,
        Func<TIn, CancellationToken, Task<Result<TOut>>> selector,
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
    public static async Task<Result<IReadOnlyList<T>>> SequenceAsync<T>(IAsyncEnumerable<Result<T>> results, CancellationToken cancellationToken = default)
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
    /// <returns>A task that resolves to a successful result containing all projected values or the first failure encountered.</returns>
    public static Task<Result<IReadOnlyList<TOut>>> TraverseAsync<TIn, TOut>(
        IAsyncEnumerable<TIn> source,
        Func<TIn, CancellationToken, Task<Result<TOut>>> selector,
        CancellationToken cancellationToken = default) => selector is null
            ? throw new ArgumentNullException(nameof(selector))
            : TraverseAsync(source, (value, token) => new ValueTask<Result<TOut>>(selector(value, token)), cancellationToken);

    /// <summary>Applies an asynchronous selector to each value in the source and aggregates the successful results.</summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source items to transform.</param>
    /// <param name="selector">The selector applied to each item.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that resolves to a successful result containing all projected values or the first failure encountered.</returns>
    public static async Task<Result<IReadOnlyList<TOut>>> TraverseAsync<TIn, TOut>(
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

    /// <summary>Projects an asynchronous sequence into a new asynchronous sequence of results, stopping on first failure.</summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source sequence to project.</param>
    /// <param name="selector">The projection applied to each item.</param>
    /// <param name="cancellationToken">The token used to cancel the projection.</param>
    /// <returns>An asynchronous sequence of results.</returns>
    public static IAsyncEnumerable<Result<TOut>> MapStreamAsync<TIn, TOut>(
        IAsyncEnumerable<TIn> source,
        Func<TIn, CancellationToken, Task<Result<TOut>>> selector,
        CancellationToken cancellationToken = default) => selector is null
            ? throw new ArgumentNullException(nameof(selector))
            : MapStreamAsync(source, (value, token) => new ValueTask<Result<TOut>>(selector(value, token)), cancellationToken);

    /// <summary>Projects an asynchronous sequence into a new asynchronous sequence of results, stopping on first failure.</summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source sequence to project.</param>
    /// <param name="selector">The projection applied to each item.</param>
    /// <param name="cancellationToken">The token used to cancel the projection.</param>
    /// <returns>An asynchronous sequence of results.</returns>
    public static async IAsyncEnumerable<Result<TOut>> MapStreamAsync<TIn, TOut>(
        IAsyncEnumerable<TIn> source,
        Func<TIn, CancellationToken, ValueTask<Result<TOut>>> selector,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        ArgumentNullException.ThrowIfNull(selector);

        var configuredSource = source.WithCancellation(cancellationToken).ConfigureAwait(false);
        await using var enumerator = configuredSource.GetAsyncEnumerator();

        while (true)
        {
            bool hasNext;
            Result<TOut> failure = default;
            var hasFailure = false;
            try
            {
                hasNext = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException oce)
            {
                failure = Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
                hasFailure = true;
                hasNext = false;
            }
            catch (Exception ex)
            {
                failure = Fail<TOut>(Error.FromException(ex));
                hasFailure = true;
                hasNext = false;
            }

            if (hasFailure)
            {
                yield return failure;
                yield break;
            }

            if (!hasNext)
            {
                yield break;
            }

            Result<TOut> mapped;
            failure = default;
            hasFailure = false;
            try
            {
                mapped = await selector(enumerator.Current, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                failure = Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
                hasFailure = true;
                mapped = default;
            }
            catch (Exception ex)
            {
                failure = Fail<TOut>(Error.FromException(ex));
                hasFailure = true;
                mapped = default;
            }

            if (hasFailure)
            {
                yield return failure;
                yield break;
            }

            yield return mapped;

            if (mapped.IsFailure)
            {
                yield break;
            }
        }
    }
}

/// <summary>
/// Represents the outcome of an operation that may succeed with <typeparamref name="T"/> or fail with an <see cref="Error"/>.
/// </summary>
public readonly record struct Result<T>
{
    private readonly T _value;

    private Result(T value, Error? error, bool isSuccess)
    {
        _value = value;
        Error = error;
        IsSuccess = isSuccess;
    }

    internal static Result<T> Success(T value)
    {
        GoDiagnostics.RecordResultSuccess();
        return new(value, null, true);
    }

    internal static Result<T> Failure(Error error)
    {
        GoDiagnostics.RecordResultFailure();
        return new(default!, error ?? Error.Unspecified(), false);
    }

    /// <summary>
    /// Gets the value. When the result represents a failure this will be the default value of <typeparamref name="T"/>.
    /// Prefer using <see cref="ValueOr(T)"/> or <see cref="ValueOrThrow()"/> to handle failure cases explicitly.
    /// </summary>
    public T Value => _value;

    /// <summary>
    /// Gets the error associated with a failed result, or <c>null</c> when the result represents success.
    /// </summary>
    public Error? Error { get; }

    /// <summary>
    /// Indicates whether the result represents success.
    /// </summary>
    public bool IsSuccess { get; }

    /// <summary>
    /// Indicates whether the result represents failure.
    /// </summary>
    public bool IsFailure => !IsSuccess;

    /// <summary>Attempts to extract the successful value.</summary>
    /// <param name="value">When this method returns, contains the value if successful; otherwise the default.</param>
    /// <returns><see langword="true"/> when the result represents success; otherwise <see langword="false"/>.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        if (IsSuccess)
        {
            value = _value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Attempts to extract the error information.</summary>
    /// <param name="error">When this method returns, contains the error when the result is a failure.</param>
    /// <returns><see langword="true"/> when the result represents failure; otherwise <see langword="false"/>.</returns>
    public bool TryGetError([NotNullWhen(true)] out Error? error)
    {
        if (IsFailure)
        {
            error = Error ?? Error.Unspecified();
            return true;
        }

        error = null;
        return false;
    }

    /// <summary>Executes the appropriate callback depending on success or failure.</summary>
    /// <param name="onSuccess">Invoked when the result represents success.</param>
    /// <param name="onFailure">Invoked when the result represents failure.</param>
    public void Switch(Action<T> onSuccess, Action<Error> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        ArgumentNullException.ThrowIfNull(onFailure);

        if (IsSuccess)
        {
            onSuccess(_value);
        }
        else
        {
            onFailure(Error!);
        }
    }

    /// <summary>Projects the result onto a new value using the provided callbacks.</summary>
    /// <typeparam name="TResult">The projected result type.</typeparam>
    /// <param name="onSuccess">Invoked when the result represents success.</param>
    /// <param name="onFailure">Invoked when the result represents failure.</param>
    /// <returns>The projected value.</returns>
    public TResult Match<TResult>(Func<T, TResult> onSuccess, Func<Error, TResult> onFailure)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        ArgumentNullException.ThrowIfNull(onFailure);

        return IsSuccess ? onSuccess(_value) : onFailure(Error!);
    }

    /// <summary>Executes asynchronous callbacks depending on success or failure.</summary>
    /// <param name="onSuccess">Invoked when the result represents success.</param>
    /// <param name="onFailure">Invoked when the result represents failure.</param>
    /// <param name="cancellationToken">The token used to cancel callback execution.</param>
    /// <returns>A task representing the asynchronous callbacks.</returns>
    public ValueTask SwitchAsync(Func<T, CancellationToken, ValueTask> onSuccess, Func<Error, CancellationToken, ValueTask> onFailure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        ArgumentNullException.ThrowIfNull(onFailure);

        cancellationToken.ThrowIfCancellationRequested();
        return IsSuccess
            ? onSuccess(_value, cancellationToken)
            : onFailure(Error!, cancellationToken);
    }

    /// <summary>Projects the result asynchronously onto a new value.</summary>
    /// <typeparam name="TResult">The projected result type.</typeparam>
    /// <param name="onSuccess">Invoked when the result represents success.</param>
    /// <param name="onFailure">Invoked when the result represents failure.</param>
    /// <param name="cancellationToken">The token used to cancel callback execution.</param>
    /// <returns>The projected value.</returns>
    public ValueTask<TResult> MatchAsync<TResult>(Func<T, CancellationToken, ValueTask<TResult>> onSuccess, Func<Error, CancellationToken, ValueTask<TResult>> onFailure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);

        ArgumentNullException.ThrowIfNull(onFailure);

        cancellationToken.ThrowIfCancellationRequested();
        return IsSuccess
            ? onSuccess(_value, cancellationToken)
            : onFailure(Error!, cancellationToken);
    }

    /// <summary>Returns the contained value when successful, otherwise returns the provided fallback.</summary>
    /// <param name="fallback">The value to return when the result represents failure.</param>
    /// <returns>The contained value or <paramref name="fallback"/>.</returns>
    public T ValueOr(T fallback) => IsSuccess ? _value : fallback;

    /// <summary>Returns the contained value when successful, otherwise evaluates the fallback factory.</summary>
    /// <param name="fallbackFactory">The factory that produces a fallback value from the error.</param>
    /// <returns>The contained value or the value produced by <paramref name="fallbackFactory"/>.</returns>
    public T ValueOr(Func<Error, T> fallbackFactory) => fallbackFactory is null
            ? throw new ArgumentNullException(nameof(fallbackFactory))
            : IsSuccess ? _value : fallbackFactory(Error!);

    /// <summary>Returns the contained value when successful, otherwise throws a <see cref="ResultException"/>.</summary>
    /// <returns>The contained value.</returns>
    public T ValueOrThrow() => IsSuccess ? _value : throw new ResultException(Error!);

    /// <summary>Converts the result into an <see cref="Optional{T}"/>.</summary>
    /// <returns>An optional representing the current result.</returns>
    public Optional<T> ToOptional() => IsSuccess ? Optional<T>.SomeUnsafe(_value) : Optional<T>.None();

    /// <summary>Supports tuple deconstruction syntax.</summary>
    /// <param name="value">When this method returns, contains the successful value or the default.</param>
    /// <param name="error">When this method returns, contains the error if the result is a failure.</param>
    public void Deconstruct(out T value, out Error? error)
    {
        value = _value;
        error = Error;
    }

    /// <inheritdoc />
    public override string ToString() => IsSuccess ? $"Ok({Value})" : $"Err({Error})";

    /// <summary>Implicit conversion from tuple for backwards compatibility with existing tuple-based APIs.</summary>
    /// <param name="tuple">The tuple representing a value/error pair.</param>
    /// <returns>A <see cref="Result{T}"/> constructed from the tuple.</returns>
    public static implicit operator Result<T>((T Value, Error? Error) tuple) =>
        tuple.Error is null ? Success(tuple.Value) : Failure(tuple.Error);

    /// <summary>Implicit conversion to tuple for interop with tuple-based code.</summary>
    /// <param name="result">The result to convert.</param>
    /// <returns>A tuple containing the value and error.</returns>
    public static implicit operator (T Value, Error? Error)(Result<T> result) => (result.Value, result.Error);
}

/// <summary>
/// Exception type thrown when converting a failed <see cref="Result{T}"/> into a value.
/// </summary>
public sealed class ResultException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ResultException"/> class.</summary>
    /// <param name="error">The error associated with the failed result.</param>
    public ResultException(Error error)
        : base(error?.Message ?? Error.Unspecified().Message, error?.Cause)
    {
        Error = error ?? Error.Unspecified();
        if (error?.Cause is not null)
        {
            HResult = error.Cause.HResult;
        }
    }

    /// <summary>Gets the <see cref="Error"/> that caused the exception to be thrown.</summary>
    public Error Error { get; }
}
