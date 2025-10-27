using System.Diagnostics.CodeAnalysis;

namespace Hugo;

/// <summary>
/// Represents an optional value that may or may not be present.
/// </summary>
public readonly record struct Optional<T>
{
    private readonly T _value;

    private Optional(T value, bool hasValue)
    {
        _value = value;
        HasValue = hasValue;
    }

    public Optional(T value)
    {
        ArgumentNullException.ThrowIfNull(value);

        _value = value;
        HasValue = true;
    }

    /// <summary>
    /// Indicates whether the optional contains a value.
    /// </summary>
    public bool HasValue { get; }

    /// <summary>
    /// Indicates whether the optional does not contain a value.
    /// </summary>
    public bool HasNoValue => !HasValue;

    /// <summary>Gets the contained value or throws when no value is present.</summary>
    /// <exception cref="InvalidOperationException">Thrown when <see cref="HasValue"/> is <see langword="false"/>.</exception>
    public T Value => HasValue ? _value : throw new InvalidOperationException("Optional has no value.");

    /// <summary>
    /// Attempts to retrieve the value.
    /// </summary>
    /// <param name="value">When this method returns, contains the value if present.</param>
    /// <returns><see langword="true"/> when the optional contains a value; otherwise <see langword="false"/>.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        if (HasValue)
        {
            value = _value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>Returns the contained value when present; otherwise returns the supplied fallback.</summary>
    /// <param name="fallback">The value to return when the optional is empty.</param>
    /// <returns>The contained value or <paramref name="fallback"/>.</returns>
    public T ValueOr(T fallback) => HasValue ? _value : fallback;

    /// <summary>Returns the contained value when present; otherwise evaluates the fallback factory.</summary>
    /// <param name="fallbackFactory">The factory invoked when the optional is empty.</param>
    /// <returns>The contained value or the value produced by <paramref name="fallbackFactory"/>.</returns>
    public T ValueOr(Func<T> fallbackFactory)
    {
        ArgumentNullException.ThrowIfNull(fallbackFactory);

        return HasValue ? _value : fallbackFactory();
    }

    /// <summary>Executes the appropriate branch depending on whether a value is present.</summary>
    /// <param name="onValue">Invoked when a value is present.</param>
    /// <param name="onNone">Invoked when the optional is empty.</param>
    /// <returns>The value produced by the executed branch.</returns>
    public TResult Match<TResult>(Func<T, TResult> onValue, Func<TResult> onNone)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        ArgumentNullException.ThrowIfNull(onNone);

        return HasValue ? onValue(_value) : onNone();
    }

    /// <summary>Executes the appropriate action depending on whether a value is present.</summary>
    /// <param name="onValue">Invoked when a value is present.</param>
    /// <param name="onNone">Invoked when the optional is empty.</param>
    public void Switch(Action<T> onValue, Action onNone)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        ArgumentNullException.ThrowIfNull(onNone);

        if (HasValue)
        {
            onValue(_value);
        }
        else
        {
            onNone();
        }
    }

    /// <summary>Maps the value when present using the provided mapper.</summary>
    /// <typeparam name="TResult">The mapped value type.</typeparam>
    /// <param name="mapper">The transformation applied to the contained value.</param>
    /// <returns>An optional containing the mapped value when present.</returns>
    public Optional<TResult> Map<TResult>(Func<T, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);

        return HasValue ? Optional.Some(mapper(_value)) : default;
    }

    /// <summary>Binds the value when present using the provided binder.</summary>
    /// <typeparam name="TResult">The bound value type.</typeparam>
    /// <param name="binder">The function invoked when a value is present.</param>
    /// <returns>The optional returned by <paramref name="binder"/> or an empty optional.</returns>
    public Optional<TResult> Bind<TResult>(Func<T, Optional<TResult>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);

        return HasValue ? binder(_value) : default;
    }

    /// <summary>Keeps the value only when the predicate returns <see langword="true"/>.</summary>
    /// <param name="predicate">The predicate evaluated against the value.</param>
    /// <returns>The current optional when the predicate succeeds; otherwise an empty optional.</returns>
    public Optional<T> Filter(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (!HasValue)
        {
            return this;
        }

        return predicate(_value) ? this : default;
    }

    /// <summary>Converts the optional into a <see cref="Result{T}"/>.</summary>
    /// <param name="errorFactory">The factory used to create an error when the optional is empty.</param>
    /// <returns>A successful result when the optional has a value; otherwise a failure.</returns>
    public Result<T> ToResult(Func<Error> errorFactory)
    {
        ArgumentNullException.ThrowIfNull(errorFactory);

        return HasValue ? Result.Ok(_value) : Result.Fail<T>(errorFactory() ?? Error.Unspecified());
    }

    /// <summary>Returns the current optional when it has a value; otherwise returns the provided alternative.</summary>
    /// <param name="alternative">The alternative optional to use when empty.</param>
    /// <returns>The current optional or <paramref name="alternative"/>.</returns>
    public Optional<T> Or(Optional<T> alternative) => HasValue ? this : alternative;

    /// <summary>Deconstructs the optional into its state and value.</summary>
    /// <param name="hasValue">When this method returns, indicates whether a value is present.</param>
    /// <param name="value">When this method returns, contains the value when present.</param>
    public void Deconstruct(out bool hasValue, [MaybeNull] out T value)
    {
        hasValue = HasValue;
        value = HasValue ? _value : default!;
    }

    /// <summary>
    /// Returns a string representation of the optional.
    /// </summary>
    public override string ToString() => HasValue ? $"Some({_value})" : "None";

    internal static Optional<T> SomeUnsafe(T value) => new(value!, true);
}

/// <summary>
/// Provides helper methods for working with optionals.
/// </summary>
public static class Optional
{
    /// <summary>Creates an optional containing the provided value.</summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The value to wrap.</param>
    /// <returns>An optional containing <paramref name="value"/>.</returns>
    public static Optional<T> Some<T>(T value) => new Optional<T>(value);

    /// <summary>
    /// Returns an empty optional.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <returns>An empty optional.</returns>
    public static Optional<T> None<T>() => default;

    /// <summary>
    /// Creates an optional from a nullable reference.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The nullable reference value.</param>
    /// <returns>An optional that is empty when <paramref name="value"/> is <see langword="null"/>.</returns>
    public static Optional<T> FromNullable<T>(T? value) where T : class =>
        value is null ? default : new Optional<T>(value);

    /// <summary>
    /// Creates an optional from a nullable value type.
    /// </summary>
    /// <typeparam name="T">The value type.</typeparam>
    /// <param name="value">The nullable value.</param>
    /// <returns>An optional that is empty when <paramref name="value"/> has no value.</returns>
    public static Optional<T> FromNullable<T>(T? value) where T : struct =>
        value.HasValue ? new Optional<T>(value.Value) : default;

    /// <summary>
    /// Converts an optional to a <see cref="Result{T}"/> using the provided error factory.
    /// </summary>
    public static Result<T> ToResult<T>(Optional<T> optional, Func<Error> errorFactory) => optional.ToResult(errorFactory);
}
