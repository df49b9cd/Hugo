namespace Hugo;

/// <summary>
/// Represents a serialized deterministic record persisted for workflow replay.
/// </summary>
public sealed class DeterministicRecord
{
    private readonly byte[] _payload;

    /// <summary>
    /// Initializes a new instance of the <see cref="DeterministicRecord"/> class.
    /// </summary>
    /// <param name="kind">Categorises the record (for example, <c>hugo.version</c> or <c>hugo.effect</c>).</param>
    /// <param name="version">Version associated with the record.</param>
    /// <param name="payload">Serialized payload for the record.</param>
    /// <param name="recordedAt">Timestamp captured by the current <see cref="TimeProvider"/>.</param>
    public DeterministicRecord(string kind, int version, ReadOnlySpan<byte> payload, DateTimeOffset recordedAt)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            throw new ArgumentException("Record kind must be provided.", nameof(kind));
        }

        Kind = kind;
        Version = version;
        RecordedAt = recordedAt;
        _payload = payload.ToArray();
    }

    /// <summary>
    /// Gets the record category.
    /// </summary>
    public string Kind { get; }

    /// <summary>
    /// Gets the version associated with the record.
    /// </summary>
    public int Version { get; }

    /// <summary>
    /// Gets the timestamp associated with the record.
    /// </summary>
    public DateTimeOffset RecordedAt { get; }

    /// <summary>
    /// Gets the serialized payload for the record.
    /// </summary>
    public ReadOnlyMemory<byte> Payload => _payload;
}
