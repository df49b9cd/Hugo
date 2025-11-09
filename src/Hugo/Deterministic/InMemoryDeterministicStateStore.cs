using System.Collections.Concurrent;

namespace Hugo;

/// <summary>
/// Provides an in-memory implementation of <see cref="IDeterministicStateStore"/> suitable for testing scenarios.
/// </summary>
public sealed class InMemoryDeterministicStateStore : IDeterministicStateStore
{
    private readonly ConcurrentDictionary<string, DeterministicRecord> _records = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public bool TryGet(string key, out DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return _records.TryGetValue(key, out record!);
    }

    /// <inheritdoc />
    public void Set(string key, DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);

        _records[key] = record;
    }

    /// <inheritdoc />
    public bool TryAdd(string key, DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);

        return _records.TryAdd(key, record);
    }
}
