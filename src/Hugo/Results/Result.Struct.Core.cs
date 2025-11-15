using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

using Hugo.Policies;

namespace Hugo;

/// <summary>
/// Represents the outcome of an operation that may succeed with <typeparamref name="T"/> or fail with an <see cref="Error"/>.
/// </summary>
public readonly partial record struct Result<T>
{
    private readonly T _value;
    private readonly CompensationScope? _compensation;

    private Result(T value, Error? error, bool isSuccess, CompensationScope? compensation = null)
    {
        _value = value;
        Error = error;
        IsSuccess = isSuccess;
        _compensation = compensation;
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

    internal static Result<T> FromExistingFailure(Error error) => new(default!, error ?? Error.Unspecified(), false);

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

    /// <summary>
    /// Reuses the current error when converting the result into another failure type without recording diagnostics again.
    /// </summary>
    /// <typeparam name="TOut">The destination result type.</typeparam>
    /// <returns>A failed result containing the same error.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the result represents success.</exception>
    public Result<TOut> CastFailure<TOut>()
    {
        if (IsSuccess)
        {
            throw new InvalidOperationException("Cannot cast a successful result to a failure.");
        }

        return Result<TOut>.FromExistingFailure(Error ?? Error.Unspecified());
    }

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

    internal CompensationScope? Compensation => _compensation;

    internal bool TryGetCompensation(out CompensationScope? compensation)
    {
        if (_compensation is { HasActions: true })
        {
            compensation = _compensation;
            return true;
        }

        compensation = null;
        return false;
    }

    /// <summary>Gets a value indicating whether the result carries compensation actions.</summary>
    internal bool HasCompensation => _compensation?.HasActions == true;

    /// <inheritdoc />
    public bool Equals(Result<T> other) =>
        EqualityComparer<T>.Default.Equals(_value, other._value)
        && EqualityComparer<Error?>.Default.Equals(Error, other.Error)
        && IsSuccess == other.IsSuccess;

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_value, Error, IsSuccess);
}
