namespace Hugo.Sagas;

/// <summary>
/// Represents mutable state shared across saga steps.
/// </summary>
public sealed class ResultSagaState
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets the key/value pairs captured by prior saga steps.</summary>
    public IReadOnlyDictionary<string, object?> Data => _values;

    /// <summary>Stores a value produced by the current step.</summary>
    /// <typeparam name="T">The type of the value being stored.</typeparam>
    /// <param name="key">The key under which the value is stored.</param>
    /// <param name="value">The value to persist.</param>
    public void Set<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _values[key] = value;
    }

    /// <summary>Attempts to retrieve a value that was previously stored.</summary>
    /// <typeparam name="T">The expected type of the stored value.</typeparam>
    /// <param name="key">The key associated with the stored value.</param>
    /// <param name="value">When this method returns, contains the retrieved value if found; otherwise the default.</param>
    /// <returns><see langword="true"/> when the key exists and can be cast to <typeparamref name="T"/>; otherwise <see langword="false"/>.</returns>
    public bool TryGet<T>(string key, out T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (_values.TryGetValue(key, out var obj) && obj is T typed)
        {
            value = typed;
            return true;
        }

        value = default!;
        return false;
    }
}
