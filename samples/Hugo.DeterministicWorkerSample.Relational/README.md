# Hugo.DeterministicWorkerSample.Relational

This sample extends the deterministic worker pipeline to a relational deployment. It wires Hugo’s deterministic coordination APIs into SQL Server, runs lightweight migrations for both deterministic state and saga projections, and shows how to keep replay guarantees intact while persisting aggregates in a real database.

## Scenario Overview

- `QueuePublisher` reuses the baseline message patterns but still runs in-process. Each message is persisted through `DeterministicPipelineProcessor` exactly once even when duplicates or out-of-order events arrive.
- `Hugo.Deterministic.SqlServer` provides the deterministic state store (`VersionGate` + `DeterministicEffectStore`). The provider ensures deterministic effects are idempotently upserted.
- `SqlServerPipelineEntityRepository` persists the saga projection in a relational table and exposes snapshots for diagnostics. `SqlServerPipelineSchemaMigrator` performs the table creation/migration on startup.
- The rest of the pipeline (`PipelineSaga`, `DeterministicPipelineProcessor`, telemetry, and deterministic capture) lives in `Hugo.DeterministicWorkerSample.Core` so other deployments can share the same logic.

## Prerequisites

- .NET 10 SDK.
- SQL Server instance (LocalDB, Docker, Azure SQL, etc.).
- Optional: OpenTelemetry collector or Prometheus endpoint if you want to forward telemetry.

## Configuration

At minimum, provide a SQL Server connection string. The sample looks for values in this order:

1. `Pipeline:SqlServer:ConnectionString` configuration entry (e.g., `appsettings.json`).
2. `Deterministic:SqlServer:ConnectionString` entry.
3. `ConnectionStrings:Pipeline` or `ConnectionStrings:Deterministic`.
4. Environment variable `HUGO_SAMPLE_SQLSERVER`.
5. Fallback development value `Server=localhost,1433;Database=HugoDeterministicSample;User ID=sa;Password=Your_password123;TrustServerCertificate=True`.

Schema/table names can be overridden via configuration:

```json
{
  "Deterministic": {
    "SqlServer": {
      "Schema": "hugo",
      "TableName": "DeterministicRecords"
    }
  },
  "Pipeline": {
    "SqlServer": {
      "Schema": "hugo",
      "TableName": "PipelineEntities"
    }
  }
}
```

## Running the Sample

```bash
 dotnet run --project samples/Hugo.DeterministicWorkerSample.Relational/Hugo.DeterministicWorkerSample.Relational.csproj
```

On startup the worker will:

1. Ensure the deterministic records table exists (via `Hugo.Deterministic.SqlServer`).
2. Run `SqlServerPipelineSchemaMigrator` to provision the projection table if missing.
3. Publish sample traffic and process it through the deterministic saga.

Every tenth iteration the pipeline prints relational snapshots so you can inspect persisted aggregates directly in SQL Server.

## Key Components

| Component | Description |
| --- | --- |
| `SqlServerPipelineEntityRepository` | Implements `IPipelineEntityRepository` using SQL Server upserts and transactions. |
| `SqlServerPipelineSchemaMigrator` | Idempotently creates/migrates the projection table at startup. |
| `Hugo.Deterministic.SqlServer` | Registers `SqlServerDeterministicStateStore` for deterministic version/effect persistence. |
| `Hugo.DeterministicWorkerSample.Core` | Shared saga processor, telemetry, and serializer metadata. |

## Extending the Sample

- Modify the SQL schema (add columns, indexes) and bump the deterministic workflow version to practice rolling upgrades.
- Swap the connection string to point at Azure SQL or a managed instance and observe that replay guarantees remain intact.
- Replace the in-process publisher with a real Kafka consumer—only the ingress changes; deterministic capture and SQL persistence stay the same.
- Integrate an external migration tool (EF Core migrations, Flyway, Liquibase) by replacing `SqlServerPipelineSchemaMigrator` with your preferred mechanism.

## Troubleshooting

- **Login failures** – Verify the connection string and credentials. For Docker SQL Server, ensure port `1433` is exposed and `TrustServerCertificate=True` when using self-signed certs.
- **Schema drift** – If you rename columns, update both the migrator and repository to keep read/write paths aligned.
- **Duplicate processing** – Ensure the deterministic table is shared between application instances; replay safety requires a single source-of-truth for captured effects.

For Cosmos DB, Redis, or other backends, see `docs/deterministic-persistence-providers.md` and the additional sample in `Hugo.DeterministicWorkerSample.CloudQueue`.
