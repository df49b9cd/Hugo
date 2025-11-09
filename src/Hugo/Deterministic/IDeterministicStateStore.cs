using System.Diagnostics.CodeAnalysis;

namespace Hugo;

/// <summary>
/// Provides durable storage for deterministic workflow state such as recorded side-effects or version markers.
/// </summary>
/// <remarks>
/// Implementations must avoid mutating the supplied <see cref="DeterministicRecord"/> instances and should guarantee
/// that reads and writes are safe for concurrent access. Stored payloads are assumed to be stable and fully
/// deterministic so they can be replayed across workflow executions.
/// </summary>
public interface IDeterministicStateStore
{
    /// <summary>
    /// Attempts to fetch a previously recorded deterministic state entry.
    /// </summary>
    /// <param name="key">Unique identifier for the state entry.</param>
    /// <param name="record">
    /// When this method returns, contains the retrieved record if present; otherwise an undefined value. The returned
    /// <see cref="DeterministicRecord"/> must be the same instance that was persisted and should not be mutated by the caller.
    /// </param>
    /// <returns><c>true</c> when a record exists; otherwise <c>false</c>.</returns>
    bool TryGet(string key, out DeterministicRecord record);

    /// <summary>
    /// Persists the supplied deterministic state entry, replacing any existing record for the key.
    /// </summary>
    /// <param name="key">Unique identifier for the state entry.</param>
    /// <param name="record">
    /// The record to persist. Implementations must store the provided instance without mutation so that subsequent reads
    /// return payloads identical to those recorded during the workflow execution.
    /// </param>
    [SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Set expresses the domain-specific operation for deterministic state stores.")]
    void Set(string key, DeterministicRecord record);

    /// <summary>
    /// Attempts to persist the supplied deterministic state entry when no record for the key exists.
    /// </summary>
    /// <param name="key">Unique identifier for the state entry.</param>
    /// <param name="record">The record to persist if the key has not been recorded.</param>
    /// <returns><c>true</c> when the record was inserted; <c>false</c> when another writer already recorded a value.</returns>
    bool TryAdd(string key, DeterministicRecord record);
}
