# Deterministic Persistence Providers

Hugo’s deterministic workflows rely on an `IDeterministicStateStore` to capture version markers and terminal effects. The core library ships with an in-memory implementation, but production scenarios need durable persistence. The provider packages introduced with the deterministic worker samples offer ready-to-use integrations for common data stores.

## Core Concepts

- **Deterministic capture** (`DeterministicWorkflowContext.CaptureAsync`) must write idempotently: the first successful execution stores the effect and every replay returns the same payload without re-running side-effects.
- **Version tracking** (`VersionGate`) records which workflow version produced a capture so you can roll out upgrades safely.
- **Serializer options** should be shared between the gate/effect store and your saga to keep metadata compatible across replays.

Each provider implements `IDeterministicStateStore` and exposes DI helpers so you can swap them into the worker samples with minimal code changes.

## SQL Server (`Hugo.Deterministic.SqlServer`)

```csharp
builder.Services.AddHugoDeterministicSqlServer(connectionString, options =>
{
    options.Schema = "hugo";           // default dbo
    options.TableName = "Effects";     // default DeterministicRecords
    options.AutoCreateTable = true;    // create table if missing
});
```

The provider stores deterministic records in a table with the following shape:

```sql
CREATE TABLE [hugo].[Effects] (
    [Key] NVARCHAR(256) NOT NULL PRIMARY KEY,
    [Kind] NVARCHAR(128) NOT NULL,
    [Version] INT NOT NULL,
    [RecordedAt] DATETIMEOFFSET NOT NULL,
    [Payload] VARBINARY(MAX) NOT NULL
);
```

**Best practices**

- Deploy migrations ahead of time in production; keep `AutoCreateTable` for development/test environments.
- Place the deterministic table in the same database as your saga projections so transactions can cover both when necessary.
- Use connection pooling defaults; the provider opens short-lived connections per call to avoid concurrency bottlenecks.

## Azure Cosmos DB (`Hugo.Deterministic.Cosmos`)

```csharp
CosmosClient cosmosClient = new(connectionString, new CosmosClientOptions
{
    ApplicationName = builder.Environment.ApplicationName
});

builder.Services.AddHugoDeterministicCosmos(options =>
{
    options.Client = cosmosClient;
    options.DatabaseId = "hugo-deterministic";
    options.ContainerId = "deterministic-effects";
    options.PartitionKeyPath = "/id";
    options.CreateResources = true;
});
```

Payloads are stored as base64 in the container so the effect remains opaque but replayable. The provider lazily initialises the database and container; disable `CreateResources` when using infrastructure-as-code.

**Best practices**

- Configure throughput (`options.Throughput`) to match expected replay/write rates.
- Use the same Cosmos client instance for deterministic storage and saga projections to share connection pooling and telemetry.
- When rolling out breaking schema changes, create a new container and run a sidecar migration or build lazy upgrade logic into the saga.

## Redis (`Hugo.Deterministic.Redis`)

```csharp
ConnectionMultiplexer redis = await ConnectionMultiplexer.ConnectAsync("localhost:6379");

builder.Services.AddHugoDeterministicRedis(options =>
{
    options.ConnectionMultiplexer = redis;
    options.KeyPrefix = "hugo:deterministic:";
    options.Database = 2;        // optional redis DB selection
    options.Expiry = null;       // set a TTL if your captures can expire
});
```

The Redis provider serialises deterministic effects to JSON and stores them under a namespaced key. It is ideal for fast-moving workloads where memory is plentiful and replay histories can be trimmed via TTLs.

**Best practices**

- Configure persistence on the Redis cluster (AOF or RDB) if deterministic captures must survive restarts.
- Set an `Expiry` when your message retention period allows deterministic effects to age out automatically.
- Consider using Redis ACLs so the worker’s keyspace access is restricted to the deterministic prefix.

## Wiring the Providers into the Worker Samples

1. Replace the in-memory deterministic state registration with the provider of your choice.
2. Register a production-ready implementation of `IPipelineEntityRepository` (SQL, Cosmos, Redis, etc.).
3. Keep `TimeProvider.System` shared across the gate, effect store, and repository so captured timestamps remain comparable.
4. Update `DeterministicPipelineSerializerContext` (and the shared serializer options) if your saga captures new models.

```csharp
builder.Services.AddHugoDeterministicSqlServer(connectionString);
builder.Services.AddSingleton<IPipelineEntityRepository, SqlServerPipelineEntityRepository>();
```

See the concrete projects for end-to-end examples:

- `samples/Hugo.DeterministicWorkerSample` – in-memory baseline.
- `samples/Hugo.DeterministicWorkerSample.Relational` – SQL Server with schema migrations.
- `samples/Hugo.DeterministicWorkerSample.CloudQueue` – Azure Queue ingestion and Cosmos persistence.

## Migration Strategies

- **Eager**: run a migration job that reads deterministic effects/projections, transforms them, and writes them back using the new schema/version.
- **Lazy**: detect legacy payloads during replay and upgrade them on the fly (ensure conversions are deterministic).
- **Parallel**: activate a new workflow version and keep the previous version around to serve old captures until they expire.

Always bump the workflow version (`DeterministicGate.Workflow(...).ForVersion(...)`) when persisted data contracts change, and keep earlier handlers around until you finish migrating historical captures.
