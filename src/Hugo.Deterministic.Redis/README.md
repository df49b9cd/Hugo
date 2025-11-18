# Hugo.Deterministic.Redis

Deterministic state store for Hugo workflows backed by Redis. Stores version gates and deterministic effects so replayed executions return consistent results.

## Install

```bash
dotnet add package Hugo.Deterministic.Redis
```

## Configure

```csharp
builder.Services.AddSingleton<IDeterministicStateStore>(sp =>
{
    var muxer = ConnectionMultiplexer.Connect("redis:6379");
    return new RedisDeterministicStateStore(muxer.GetDatabase());
});
```

## Notes

- Uses Redis hashes per change id; optimistic add/overwrite semantics mirror other deterministic stores.
- Ensure keyspace persistence (AOF/RDB) matches your durability requirements.

