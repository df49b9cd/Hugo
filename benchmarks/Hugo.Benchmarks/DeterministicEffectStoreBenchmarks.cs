using BenchmarkDotNet.Attributes;
using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Results, BenchmarkCategories.Deterministic)]
public class DeterministicEffectStoreBenchmarks
{
    [Params(128, 512)]
    public int OperationCount { get; set; }

    [Params(256, 96_000)]
    public int PayloadBytes { get; set; }

    private InMemoryDeterministicStateStore? _store;
    private DeterministicEffectStore? _effectStore;
    private string[]? _effectIds;

    [GlobalSetup]
    public void AllocateIds()
    {
        _effectIds = new string[512];
        for (var i = 0; i < _effectIds.Length; i++)
        {
            _effectIds[i] = $"effect-{i}";
        }
    }

    [IterationSetup(Target = nameof(Capture_NewEffectsAsync))]
    public void SetupCapture()
    {
        _store = new InMemoryDeterministicStateStore();
        _effectStore = DeterministicEffectStore.CreateDefault(_store);
    }

    [IterationSetup(Target = nameof(Replay_CachedEffectsAsync))]
    public async Task SetupReplayAsync()
    {
        _store = new InMemoryDeterministicStateStore();
        _effectStore = DeterministicEffectStore.CreateDefault(_store);

        if (_effectIds is null)
        {
            throw new InvalidOperationException("Effect ids not initialized.");
        }

        for (var i = 0; i < OperationCount; i++)
        {
            var payload = CreatePayload();
            await _effectStore.CaptureAsync(_effectIds[i], _ => ValueTask.FromResult(Result.Ok(payload))).ConfigureAwait(false);
        }
    }

    [Benchmark(Baseline = true)]
    public async Task Capture_NewEffectsAsync()
    {
        if (_effectStore is null || _effectIds is null)
        {
            throw new InvalidOperationException("Effect store not initialized.");
        }

        for (var i = 0; i < OperationCount; i++)
        {
            var value = CreatePayload();
            await _effectStore.CaptureAsync(_effectIds[i], _ => ValueTask.FromResult(Result.Ok(value))).ConfigureAwait(false);
        }
    }

    [Benchmark]
    public async Task Replay_CachedEffectsAsync()
    {
        if (_effectStore is null || _effectIds is null)
        {
            throw new InvalidOperationException("Effect store not initialized.");
        }

        for (var i = 0; i < OperationCount; i++)
        {
            // The second invocation should replay the cached value without invoking the factory.
            var value = CreatePayload();
            await _effectStore.CaptureAsync(_effectIds[i], _ => ValueTask.FromResult(Result.Ok(value))).ConfigureAwait(false);
        }
    }

    private byte[] CreatePayload()
    {
        var buffer = new byte[PayloadBytes];
        for (int i = 0; i < buffer.Length; i += 64)
        {
            buffer[i] = (byte)(i % 251);
        }

        return buffer;
    }
}
