namespace Hugo;

/// <summary>
/// A helper struct that enables the <c>defer</c> functionality via the <see cref="IDisposable"/> pattern.
/// </summary>
public readonly struct Defer(Action action) : IDisposable, IEquatable<Defer>
{
    private readonly Action _action = action ?? throw new ArgumentNullException(nameof(action));

    /// <summary>Invokes the deferred action.</summary>
    public void Dispose() => _action();

    public override bool Equals(object? obj) => obj is Defer other && Equals(other);

    public override int GetHashCode() => _action.GetHashCode();

    public static bool operator ==(Defer left, Defer right) => left.Equals(right);

    public static bool operator !=(Defer left, Defer right) => !left.Equals(right);

    public bool Equals(Defer other) => ReferenceEquals(_action, other._action);
}
