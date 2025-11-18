using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;

using StackExchange.Redis;

namespace Hugo.Deterministic.Redis;

/// <summary>
/// Options for configuring <see cref="RedisDeterministicStateStore"/>.
/// </summary>
public sealed class RedisDeterministicStateStoreOptions
{
    /// <summary>
    /// Gets or sets the connection multiplexer used to access Redis.
    /// </summary>
    public IConnectionMultiplexer? ConnectionMultiplexer { get; set; }

    /// <summary>
    /// Gets or sets the key prefix used when storing deterministic records.
    /// </summary>
    public string KeyPrefix { get; set; } = "hugo:deterministic:";

    /// <summary>
    /// Gets or sets the Redis database number. Defaults to <c>-1</c> (use connection default).
    /// </summary>
    public int Database { get; set; } = -1;

    /// <summary>
    /// Gets or sets an optional expiry applied when writing records. <c>null</c> disables expiration.
    /// </summary>
    public TimeSpan? Expiry { get; set; }
}

/// <summary>
/// Redis implementation of <see cref="IDeterministicStateStore"/>.
/// </summary>
public sealed class RedisDeterministicStateStore : IDeterministicStateStore
{
    private readonly RedisDeterministicStateStoreOptions _options;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="RedisDeterministicStateStore"/> class.
    /// </summary>
    /// <param name="options">Redis configuration.</param>
    public RedisDeterministicStateStore(RedisDeterministicStateStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        if (options.ConnectionMultiplexer is null)
        {
            throw new ArgumentException("ConnectionMultiplexer must be supplied.", nameof(options));
        }
    }

    /// <inheritdoc />
    public bool TryGet(string key, out DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        IDatabase database = GetDatabase();
        RedisValue value = database.StringGet(BuildKey(key));
        if (!value.HasValue)
        {
            record = null!;
            return false;
        }

        DeterministicPayload? payload = DeserializePayload(value);
        if (payload is null)
        {
            record = null!;
            return false;
        }

        record = payload.ToRecord();
        return true;
    }

    /// <inheritdoc />
    public void Set(string key, DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);

        IDatabase database = GetDatabase();
        DeterministicPayload payload = DeterministicPayload.FromRecord(record);

        string json = SerializePayload(payload);
        database.StringSet(BuildKey(key), json, _options.Expiry);
    }

    /// <inheritdoc />
    public bool TryAdd(string key, DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);

        IDatabase database = GetDatabase();
        DeterministicPayload payload = DeterministicPayload.FromRecord(record);
        string json = SerializePayload(payload);
        return database.StringSet(BuildKey(key), json, _options.Expiry, When.NotExists);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Deterministic payload serialization uses System.Text.Json.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Deterministic payload serialization uses System.Text.Json.")]
    private string SerializePayload(DeterministicPayload payload) => JsonSerializer.Serialize(payload, _serializerOptions);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Deterministic payload serialization uses System.Text.Json.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Deterministic payload serialization uses System.Text.Json.")]
    private DeterministicPayload? DeserializePayload(RedisValue value) =>
        JsonSerializer.Deserialize<DeterministicPayload>(value!.ToString(), _serializerOptions);

    private IDatabase GetDatabase() =>
        _options.ConnectionMultiplexer!.GetDatabase(_options.Database);

    private string BuildKey(string key) => $"{_options.KeyPrefix}{key}";

    private sealed record class DeterministicPayload(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("recordedAt")] DateTimeOffset RecordedAt,
        [property: JsonPropertyName("payload")] string PayloadBase64)
    {
        public DeterministicRecord ToRecord()
        {
            byte[] payload = Convert.FromBase64String(PayloadBase64);
            return new DeterministicRecord(Kind, Version, payload, RecordedAt);
        }

        public static DeterministicPayload FromRecord(DeterministicRecord record) =>
            new(
                record.Kind,
                record.Version,
                record.RecordedAt,
                Convert.ToBase64String(record.Payload.Span));
    }
}

/// <summary>
/// Service collection extensions for configuring the Redis deterministic state store.
/// </summary>
public static class RedisServiceCollectionExtensions
{
    /// <summary>
    /// Adds a Redis-backed deterministic state store to the service collection.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <param name="configure">Delegate responsible for configuring the store options.</param>
    /// <returns>The <paramref name="services"/> instance to support chaining.</returns>
    public static IServiceCollection AddHugoDeterministicRedis(
        this IServiceCollection services,
        Action<RedisDeterministicStateStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        RedisDeterministicStateStoreOptions options = new();
        configure(options);

        services.AddSingleton(options);
        services.AddSingleton<IDeterministicStateStore, RedisDeterministicStateStore>();
        return services;
    }
}
