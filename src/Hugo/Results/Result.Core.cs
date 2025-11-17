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
    /// <param name="errorFactory">An optional factory that converts exceptions into errors.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A successful result containing the operation output or a failure wrapping the thrown exception.</returns>
    public static async ValueTask<Result<T>> TryAsync<T>(Func<CancellationToken, ValueTask<T>> operation, Func<Exception, Error?>? errorFactory = null, CancellationToken cancellationToken = default)
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
}
