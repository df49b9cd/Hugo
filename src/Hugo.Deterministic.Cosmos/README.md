# Hugo.Deterministic.Cosmos

Deterministic state store implementation for Hugo workflows backed by Azure Cosmos DB. It persists version gates and deterministic effects so replayed workflows return the same outcomes.

## Install

```bash
dotnet add package Hugo.Deterministic.Cosmos
```

## Configure

```csharp
builder.Services.AddSingleton<IDeterministicStateStore>(sp =>
{
    CosmosClient client = new("https://your-account.documents.azure.com", "key");
    return new CosmosDeterministicStateStore(client, databaseId: "hugo", containerId: "deterministic");
});
```

## Notes

- Requires an existing Cosmos DB container with partition key `/changeId`.
- Supports optimistic writes for gates and effect records; conflicting writes return `error.version.conflict`.

