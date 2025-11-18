namespace Hugo;

public readonly partial record struct Result<T>
{
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
    public Optional<T> ToOptional() => IsSuccess ? Optional<T>.SomeUnsafe(_value) : Optional.None<T>();

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

    /// <summary>Returns the current result instance.</summary>
    /// <returns>The current instance, enabling fluent APIs that expect explicit result values.</returns>
    public Result<T> ToResult() => this;

    /// <summary>Converts the result to a tuple representation.</summary>
    /// <returns>A tuple containing the value and error for interop with tuple-based APIs.</returns>
    public (T Value, Error? Error) ToValueTuple() => (Value, Error);
}
