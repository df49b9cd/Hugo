namespace Hugo;

/// <summary>
/// Provides durable storage for deterministic workflow state such as recorded side-effects or version markers.
/// </summary>
public interface IDeterministicStateStore
{
    /// <summary>
    /// Attempts to fetch a previously recorded deterministic state entry.
    /// </summary>
    /// <param name="key">Unique identifier for the state entry.</param>
    /// <param name="record">The retrieved record if present.</param>
    /// <returns><c>true</c> when a record exists; otherwise <c>false</c>.</returns>
    bool TryGet(string key, out DeterministicRecord record);

    /// <summary>
    /// Persists the supplied deterministic state entry, replacing any existing record for the key.
    /// </summary>
    /// <param name="key">Unique identifier for the state entry.</param>
    /// <param name="record">The record to persist.</param>
    void Set(string key, DeterministicRecord record);
}
