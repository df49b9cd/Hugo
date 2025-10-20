namespace Hugo.Sagas;

/// <summary>
/// Represents mutable state shared across saga steps.
/// </summary>
public sealed class ResultSagaState
{
    private readonly Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyDictionary<string, object?> Data => _values;

    public void Set<T>(string key, T value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        _values[key] = value;
    }

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
