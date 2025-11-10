using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Hugo;
using Hugo.DeterministicWorkerSample.Core;

using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hugo.DeterministicWorkerSample.CloudQueue;

/// <summary>
/// Options controlling how pipeline projections are stored in Azure Cosmos DB.
/// </summary>
internal sealed class CosmosPipelineOptions
{
    /// <summary>
    /// Gets or sets the database identifier.
    /// </summary>
    public string DatabaseId { get; set; } = "hugo-deterministic";

    /// <summary>
    /// Gets or sets the container identifier.
    /// </summary>
    public string ContainerId { get; set; } = "pipeline-projections";

    /// <summary>
    /// Gets or sets the partition key path. Defaults to <c>/id</c>.
    /// </summary>
    public string PartitionKeyPath { get; set; } = "/id";

    /// <summary>
    /// Gets or sets a value indicating whether database/container resources should be created automatically.
    /// </summary>
    public bool CreateResources { get; set; } = true;
}

/// <summary>
/// Cosmos DB implementation of <see cref="IPipelineEntityRepository"/> used by the cloud queue sample.
/// </summary>
internal sealed class CosmosPipelineEntityRepository : IPipelineEntityRepository
{
    private readonly CosmosClient _client;
    private readonly CosmosPipelineOptions _options;
    private readonly ILogger<CosmosPipelineEntityRepository> _logger;
    private Container? _container;
    private Task? _initializationTask;
    private readonly object _initializationLock = new();

    public CosmosPipelineEntityRepository(
        CosmosClient client,
        IOptions<CosmosPipelineOptions> options,
        ILogger<CosmosPipelineEntityRepository> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async ValueTask<Result<PipelineEntity>> LoadAsync(string entityId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityId);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        Container container = _container!;
        try
        {
            ItemResponse<PipelineDocument> response = await container.ReadItemAsync<PipelineDocument>(entityId, new PartitionKey(entityId), cancellationToken: cancellationToken).ConfigureAwait(false);
            return Result.Ok(response.Resource.ToEntity());
        }
        catch (CosmosException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return Result.Ok(PipelineEntity.Create(entityId));
        }
    }

    public async ValueTask<Result<PipelineEntity>> SaveAsync(PipelineEntity entity, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entity);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        Container container = _container!;
        PipelineDocument document = PipelineDocument.FromEntity(entity);
        await container.UpsertItemAsync(document, new PartitionKey(document.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
        return Result.Ok(entity);
    }

    public IReadOnlyList<PipelineEntity> Snapshot()
    {
        try
        {
            EnsureInitializedAsync(CancellationToken.None).GetAwaiter().GetResult();
            Container container = _container!;

            QueryDefinition query = new("SELECT * FROM c");
            using FeedIterator<PipelineDocument> iterator = container.GetItemQueryIterator<PipelineDocument>(query, requestOptions: new QueryRequestOptions
            {
                PartitionKey = null
            });

            List<PipelineEntity> entities = new();
            while (iterator.HasMoreResults)
            {
                FeedResponse<PipelineDocument> response = iterator.ReadNextAsync().GetAwaiter().GetResult();
                entities.AddRange(response.Select(static doc => doc.ToEntity()));
            }

            return entities.OrderBy(static e => e.EntityId, StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch (CosmosException ex)
        {
            _logger.LogError(ex, "Failed to snapshot pipeline projections from Cosmos DB.");
            return Array.Empty<PipelineEntity>();
        }
    }

    private Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_container is not null)
        {
            return Task.CompletedTask;
        }

        lock (_initializationLock)
        {
            _initializationTask ??= InitializeAsync(cancellationToken);
        }

        return _initializationTask!;
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Database database;
        if (_options.CreateResources)
        {
            DatabaseResponse databaseResponse = await _client.CreateDatabaseIfNotExistsAsync(_options.DatabaseId, cancellationToken: cancellationToken).ConfigureAwait(false);
            database = databaseResponse.Database;

            ContainerProperties properties = new(_options.ContainerId, _options.PartitionKeyPath);
            ContainerResponse containerResponse = await database.CreateContainerIfNotExistsAsync(properties, cancellationToken: cancellationToken).ConfigureAwait(false);
            _container = containerResponse.Container;
            _logger.LogInformation("Cosmos container {ContainerId} ready for pipeline projections.", _options.ContainerId);
        }
        else
        {
            database = _client.GetDatabase(_options.DatabaseId);
            _container = database.GetContainer(_options.ContainerId);
        }
    }

    private sealed record class PipelineDocument(string Id, double RunningTotal, int ProcessedCount, double LastAmount, DateTimeOffset LastUpdated)
    {
        public PipelineEntity ToEntity() => new(Id, RunningTotal, ProcessedCount, LastAmount, LastUpdated);

        public static PipelineDocument FromEntity(PipelineEntity entity) => new(entity.EntityId, entity.RunningTotal, entity.ProcessedCount, entity.LastAmount, entity.LastUpdated);
    }
}
