using System.Text.Json;
using System.Text.Json.Serialization;

using Hugo;

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;

namespace Hugo.Deterministic.Cosmos;

/// <summary>
/// Options for configuring <see cref="CosmosDeterministicStateStore"/>.
/// </summary>
public sealed class CosmosDeterministicStateStoreOptions
{
    /// <summary>
    /// Gets or sets the <see cref="CosmosClient"/> used for data access.
    /// </summary>
    public CosmosClient? Client { get; set; }

    /// <summary>
    /// Gets or sets the database identifier used to store deterministic records.
    /// </summary>
    public string DatabaseId { get; set; } = "hugo-deterministic";

    /// <summary>
    /// Gets or sets the container identifier used to store deterministic records.
    /// </summary>
    public string ContainerId { get; set; } = "records";

    /// <summary>
    /// Gets or sets the partition key path for the deterministic container. Defaults to <c>/id</c>.
    /// </summary>
    public string PartitionKeyPath { get; set; } = "/id";

    /// <summary>
    /// Gets or sets the optional throughput (RU/s) provisioned when creating the container automatically.
    /// </summary>
    public int? Throughput { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the database and container should be created automatically.
    /// </summary>
    public bool CreateResources { get; set; } = true;
}

/// <summary>
/// Azure Cosmos DB implementation of <see cref="IDeterministicStateStore"/>.
/// </summary>
public sealed class CosmosDeterministicStateStore : IDeterministicStateStore
{
    private readonly CosmosDeterministicStateStoreOptions _options;
    private readonly CosmosClient _client;
    private Container? _container;
    private Task? _initializationTask;
    private readonly object _initializationLock = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="CosmosDeterministicStateStore"/> class.
    /// </summary>
    /// <param name="options">Options describing the target container.</param>
    public CosmosDeterministicStateStore(CosmosDeterministicStateStoreOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _client = options.Client ?? throw new ArgumentException("CosmosClient must be supplied.", nameof(options));
    }

    /// <inheritdoc />
    public bool TryGet(string key, out DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        EnsureInitialized();

        try
        {
            ItemResponse<DeterministicDocument> response = _container!.ReadItemAsync<DeterministicDocument>(key, new PartitionKey(key))
                .GetAwaiter()
                .GetResult();

            DeterministicDocument document = response.Resource;
            record = document.ToRecord();
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            record = null!;
            return false;
        }
    }

    /// <inheritdoc />
    public void Set(string key, DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);
        EnsureInitialized();

        DeterministicDocument document = DeterministicDocument.FromRecord(key, record);
        _container!.UpsertItemAsync(document, new PartitionKey(key)).GetAwaiter().GetResult();
    }

    /// <inheritdoc />
    public bool TryAdd(string key, DeterministicRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(record);
        EnsureInitialized();

        DeterministicDocument document = DeterministicDocument.FromRecord(key, record);
        try
        {
            _container!.CreateItemAsync(document, new PartitionKey(key)).GetAwaiter().GetResult();
            return true;
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return false;
        }
    }

    private void EnsureInitialized()
    {
        if (_container is not null)
        {
            return;
        }

        lock (_initializationLock)
        {
            _initializationTask ??= InitializeAsync();
        }

        _initializationTask.GetAwaiter().GetResult();
    }

    private async Task InitializeAsync()
    {
        Database database;
        if (_options.CreateResources)
        {
            DatabaseResponse databaseResponse = await _client.CreateDatabaseIfNotExistsAsync(_options.DatabaseId).ConfigureAwait(false);
            database = databaseResponse.Database;

            ContainerProperties properties = new(_options.ContainerId, _options.PartitionKeyPath);
            ContainerResponse containerResponse = await database.CreateContainerIfNotExistsAsync(
                properties,
                throughput: _options.Throughput).ConfigureAwait(false);
            _container = containerResponse.Container;
        }
        else
        {
            database = _client.GetDatabase(_options.DatabaseId);
            _container = database.GetContainer(_options.ContainerId);
        }
    }

    private sealed record class DeterministicDocument(
        [property: JsonPropertyName("id")] string Id,
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

        public static DeterministicDocument FromRecord(string key, DeterministicRecord record) =>
            new(
                key,
                record.Kind,
                record.Version,
                record.RecordedAt,
                Convert.ToBase64String(record.Payload.Span));
    }
}

/// <summary>
/// Service collection extensions for registering the Cosmos deterministic state store.
/// </summary>
public static class CosmosServiceCollectionExtensions
{
    /// <summary>
    /// Adds a Cosmos-backed deterministic state store to the service collection.
    /// </summary>
    /// <param name="services">The service collection to augment.</param>
    /// <param name="configure">Configuration delegate used to set up the store options.</param>
    /// <returns>The <paramref name="services"/> instance to support chaining.</returns>
    public static IServiceCollection AddHugoDeterministicCosmos(
        this IServiceCollection services,
        Action<CosmosDeterministicStateStoreOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        CosmosDeterministicStateStoreOptions options = new();
        configure(options);

        services.AddSingleton(options);
        services.AddSingleton<IDeterministicStateStore, CosmosDeterministicStateStore>();
        return services;
    }
}
