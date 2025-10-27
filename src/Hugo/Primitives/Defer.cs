namespace Hugo;

/// <summary>
/// A helper struct that enables the <c>defer</c> functionality via the <see cref="IDisposable"/> pattern.
/// </summary>
public readonly struct Defer(Action action) : IDisposable
{
    private readonly Action _action = action ?? throw new ArgumentNullException(nameof(action));

    /// <summary>Invokes the deferred action.</summary>
    public void Dispose() => _action();

    public override bool Equals(object obj)
    {
        throw new NotImplementedException();
    }

    public override int GetHashCode()
    {
        throw new NotImplementedException();
    }

    public static bool operator ==(Defer left, Defer right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(Defer left, Defer right)
    {
        return !(left == right);
    }
}
