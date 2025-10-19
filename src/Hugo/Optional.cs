using System.Diagnostics.CodeAnalysis;
using Hugo;

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

    /// <summary>
    /// Creates an optional with the provided value.
    /// </summary>
    public static Optional<T> Some(T value)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        return new Optional<T>(value, true);
    }

    /// <summary>
    /// Gets an optional without a value.
    /// </summary>
    public static Optional<T> None() => default;

    /// <summary>
    /// Indicates whether the optional contains a value.
    /// </summary>
    public bool HasValue { get; }

    /// <summary>
    /// Indicates whether the optional does not contain a value.
    /// </summary>
    public bool HasNoValue => !HasValue;

    /// <summary>
    /// Gets the contained value or throws when no value is present.
    /// </summary>
    public T Value => HasValue ? _value : throw new InvalidOperationException("Optional has no value.");

    /// <summary>
    /// Attempts to retrieve the value.
    /// </summary>
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

    /// <summary>
    /// Returns the contained value when present, otherwise the fallback.
    /// </summary>
    public T ValueOr(T fallback) => HasValue ? _value : fallback;

    /// <summary>
    /// Returns the contained value when present, otherwise evaluates the fallback factory.
    /// </summary>
    public T ValueOr(Func<T> fallbackFactory)
    {
        ArgumentNullException.ThrowIfNull(fallbackFactory);

        return HasValue ? _value : fallbackFactory();
    }

    /// <summary>
    /// Executes the appropriate branch depending on whether a value is present.
    /// </summary>
    public TResult Match<TResult>(Func<T, TResult> onValue, Func<TResult> onNone)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        ArgumentNullException.ThrowIfNull(onNone);

        return HasValue ? onValue(_value) : onNone();
    }

    /// <summary>
    /// Executes the appropriate action depending on whether a value is present.
    /// </summary>
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

    /// <summary>
    /// Maps the value when present using the provided mapper.
    /// </summary>
    public Optional<TResult> Map<TResult>(Func<T, TResult> mapper)
    {
        ArgumentNullException.ThrowIfNull(mapper);

        return HasValue ? Optional<TResult>.Some(mapper(_value)) : Optional<TResult>.None();
    }

    /// <summary>
    /// Binds the value when present using the provided binder.
    /// </summary>
    public Optional<TResult> Bind<TResult>(Func<T, Optional<TResult>> binder)
    {
        ArgumentNullException.ThrowIfNull(binder);

        return HasValue ? binder(_value) : Optional<TResult>.None();
    }

    /// <summary>
    /// Keeps the value only when the predicate returns true.
    /// </summary>
    public Optional<T> Filter(Func<T, bool> predicate)
    {
        ArgumentNullException.ThrowIfNull(predicate);

        if (!HasValue)
        {
            return this;
        }

        return predicate(_value) ? this : None();
    }

    /// <summary>
    /// Converts the optional into a <see cref="Result{T}"/>.
    /// </summary>
    public Result<T> ToResult(Func<Error> errorFactory)
    {
        ArgumentNullException.ThrowIfNull(errorFactory);

        return HasValue ? Result.Ok(_value) : Result.Fail<T>(errorFactory() ?? Error.Unspecified());
    }

    /// <summary>
    /// Returns the current optional when it has a value, otherwise the provided alternative.
    /// </summary>
    public Optional<T> Or(Optional<T> alternative) => HasValue ? this : alternative;

    /// <summary>
    /// Deconstructs the optional into its state and value.
    /// </summary>
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
    /// <summary>
    /// Creates an optional containing the provided value.
    /// </summary>
    public static Optional<T> Some<T>(T value) => Optional<T>.Some(value);

    /// <summary>
    /// Returns an empty optional.
    /// </summary>
    public static Optional<T> None<T>() => Optional<T>.None();

    /// <summary>
    /// Creates an optional from a nullable reference.
    /// </summary>
    public static Optional<T> FromNullable<T>(T? value) where T : class => value is null ? Optional<T>.None() : Optional<T>.Some(value);

    /// <summary>
    /// Creates an optional from a nullable value type.
    /// </summary>
    public static Optional<T> FromNullable<T>(T? value) where T : struct => value.HasValue ? Optional<T>.Some(value.Value) : Optional<T>.None();

    /// <summary>
    /// Converts an optional to a <see cref="Result{T}"/> using the provided error factory.
    /// </summary>
    public static Result<T> ToResult<T>(Optional<T> optional, Func<Error> errorFactory) => optional.ToResult(errorFactory);
}
