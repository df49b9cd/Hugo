using BenchmarkDotNet.Attributes;

using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Results, BenchmarkCategories.Deterministic)]
public class DeterministicStateStoreBenchmarks
{
    [Params(256, 4096)]
    public int PayloadBytes { get; set; }

    private DeterministicRecord _record = null!;
    private byte[] _buffer = null!;

    [GlobalSetup]
    public void Setup()
    {
        _buffer = new byte[PayloadBytes];
        for (int i = 0; i < _buffer.Length; i += 64)
        {
            _buffer[i] = (byte)(i % 251);
        }

        _record = CreateRecord(_buffer);
    }

    [Benchmark(Baseline = true)]
    public bool InMemoryStore_TryAddAndGet()
    {
        var store = new InMemoryDeterministicStateStore();
        var key = "effect-key";

        var added = store.TryAdd(key, _record);
        _ = store.TryGet(key, out var fetched);

        return added && fetched.Payload.Length == _record.Payload.Length;
    }

    [Benchmark]
    public bool InMemoryStore_SerializationClone()
    {
        var store = new InMemoryDeterministicStateStore();
        var key = "effect-key";
        var cloned = CloneRecord(_record);

        var added = store.TryAdd(key, cloned);
        _ = store.TryGet(key, out var fetched);

        return added && fetched.Payload.Length == cloned.Payload.Length;
    }

    [Benchmark]
    public bool InMemoryStore_JsonRoundTrip()
    {
        var store = new InMemoryDeterministicStateStore();
        var key = "effect-key";

        var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(_record);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<DeterministicRecord>(json);

        if (roundTripped is null)
        {
            return false;
        }

        var added = store.TryAdd(key, roundTripped);
        _ = store.TryGet(key, out var fetched);

        return added && fetched.Payload.Length == roundTripped.Payload.Length;
    }

    private static DeterministicRecord CloneRecord(DeterministicRecord record)
    {
        var buffer = record.Payload.ToArray();
        return CreateRecord(buffer);
    }

    private static DeterministicRecord CreateRecord(byte[] payload) =>
        new DeterministicRecord("hugo.effect", version: 1, payload, DateTimeOffset.UnixEpoch);
}
