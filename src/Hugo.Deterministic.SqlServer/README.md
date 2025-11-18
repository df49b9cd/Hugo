# Hugo.Deterministic.SqlServer

Deterministic state store for Hugo workflows backed by SQL Server. Persists version gates and deterministic effects to guarantee replay-safe outcomes.

## Install

```bash
dotnet add package Hugo.Deterministic.SqlServer
```

## Configure

```csharp
builder.Services.AddSingleton<IDeterministicStateStore>(sp =>
    new SqlServerDeterministicStateStore(
        connectionString: builder.Configuration.GetConnectionString("hugo-sql")));
```

## Notes

- Uses a `DeterministicRecords` table by default; override via `SqlServerDeterministicStateStoreOptions.TableName`.
- Expects primary key `(changeId, recordKind)` and unique constraint on `(changeId, recordKind)` to enforce optimistic inserts.

