namespace Hugo.Primitives;

/// <summary>
/// A helper struct that enables the <c>defer</c> functionality via the <see cref="IDisposable"/> pattern.
/// </summary>
public readonly struct Defer(Action action) : IDisposable
{
    private readonly Action _action = action ?? throw new ArgumentNullException(nameof(action));

    public void Dispose() => _action();
}
