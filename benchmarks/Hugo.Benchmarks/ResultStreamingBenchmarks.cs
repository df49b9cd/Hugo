using System.Runtime.CompilerServices;
using System.Threading.Channels;

using BenchmarkDotNet.Attributes;

using Hugo;

using Unit = Hugo.Go.Unit;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Results, BenchmarkCategories.Channels)]
public class ResultStreamingBenchmarks
{
    private const int FanWriterCount = 3;
    private const int FanInSourceCount = 3;
    private const int PartitionChannelCapacity = 256;
    private const int WindowSize = 8;

    [Params(64, 256)]
    public int ItemCount { get; set; }

    private int[] _values = Array.Empty<int>();
    private Result<int>[] _successResults = Array.Empty<Result<int>>();
    private Result<int>[] _mixedResults = Array.Empty<Result<int>>();
    private Result<int>[] _failureResults = Array.Empty<Result<int>>();
    private int _mapFailureIndex;
    private Error _failureError = Error.Unspecified();

    [GlobalSetup]
    public void Setup()
    {
        _values = Enumerable.Range(0, ItemCount).ToArray();
        _successResults = CreateResults(static i => Result.Ok(i));
        _mixedResults = CreateResults(i => i % 7 == 6 ? Result.Fail<int>(Error.From("mixed-failure", ErrorCodes.Validation)) : Result.Ok(i));
        _failureResults = CreateResults(static _ => Result.Fail<int>(Error.From("fail", ErrorCodes.Validation)));
        _mapFailureIndex = Math.Max(1, ItemCount / 2);
        _failureError = Error.From("bench-failure", ErrorCodes.Validation);
    }

    [Benchmark]
    public async Task MapStream_AllSuccessAsync()
    {
        var sum = 0;

        await foreach (var result in Result.MapStreamAsync(SuccessfulValuesAsync(CancellationToken.None), MapSelectorAsync, CancellationToken.None).ConfigureAwait(false))
        {
            if (result.IsSuccess)
            {
                sum += result.Value;
            }
        }

        GC.KeepAlive(sum);
    }

    [Benchmark]
    public async Task MapStream_FailureShortCircuitAsync()
    {
        var count = 0;

        await foreach (var result in Result.MapStreamAsync(SuccessfulValuesAsync(CancellationToken.None), MapSelectorWithFailureAsync, CancellationToken.None).ConfigureAwait(false))
        {
            if (result.IsFailure)
            {
                break;
            }

            count++;
        }

        GC.KeepAlive(count);
    }

    [Benchmark]
    public async Task FlatMapStream_AllSuccessAsync()
    {
        var total = 0;

        await foreach (var projected in Result.FlatMapStreamAsync(
                           SuccessfulValuesAsync(CancellationToken.None),
                           static (value, ct) => InnerStreamAsync(value, includeFailure: false, ct),
                           CancellationToken.None).ConfigureAwait(false))
        {
            if (projected.IsSuccess)
            {
                total += projected.Value;
            }
        }

        GC.KeepAlive(total);
    }

    [Benchmark]
    public async Task FlatMap_InnerFailureAsync()
    {
        var observed = 0;

        await foreach (var projected in Result.FlatMapStreamAsync(
                           SuccessfulValuesAsync(CancellationToken.None),
                           (value, ct) => InnerStreamAsync(value, includeFailure: value == _mapFailureIndex, ct),
                           CancellationToken.None).ConfigureAwait(false))
        {
            if (projected.IsFailure)
            {
                break;
            }

            observed++;
        }

        GC.KeepAlive(observed);
    }

    [Benchmark]
    public async Task FilterStream_PredicateAsync()
    {
        var sum = 0;

        await foreach (var result in Result.FilterStreamAsync(MixedResultStreamAsync(CancellationToken.None), static value => (value & 1) == 0, CancellationToken.None).ConfigureAwait(false))
        {
            if (result.IsSuccess)
            {
                sum += result.Value;
            }
        }

        GC.KeepAlive(sum);
    }

    [Benchmark]
    public async Task ToChannel_UnboundedAsync()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();

        await Result.ToChannelAsync(SuccessResultStreamAsync(CancellationToken.None), channel.Writer, CancellationToken.None).ConfigureAwait(false);

        var sum = 0;
        await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (item.IsSuccess)
            {
                sum += item.Value;
            }
        }

        GC.KeepAlive(sum);
    }

    [Benchmark]
    public async Task ReadAll_ChannelDrainAsync()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();

        foreach (var result in _mixedResults)
        {
            await channel.Writer.WriteAsync(result, CancellationToken.None).ConfigureAwait(false);
        }

        channel.Writer.TryComplete();

        var failures = 0;
        await foreach (var result in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (result.IsFailure)
            {
                failures++;
            }
        }

        GC.KeepAlive(failures);
    }

    [Benchmark]
    public async Task FanIn_MergeSourcesAsync()
    {
        var channel = Channel.CreateUnbounded<Result<int>>();
        var sources = CreateFanInSources(CancellationToken.None);

        var result = await Result.FanInAsync(sources, channel.Writer, CancellationToken.None).ConfigureAwait(false);

        var sum = 0;
        await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (item.IsSuccess)
            {
                sum += item.Value;
            }
        }

        GC.KeepAlive(sum);
        GC.KeepAlive(result);
    }

    [Benchmark]
    public async Task FanOut_BroadcastAsync()
    {
        var channels = new Channel<Result<int>>[FanWriterCount];
        for (var i = 0; i < channels.Length; i++)
        {
            channels[i] = Channel.CreateUnbounded<Result<int>>();
        }

        await SuccessResultStreamAsync(CancellationToken.None).FanOutAsync(GetWriters(channels), CancellationToken.None).ConfigureAwait(false);

        var total = 0;
        foreach (var channel in channels)
        {
            await foreach (var item in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (item.IsSuccess)
                {
                    total += item.Value;
                }
            }
        }

        GC.KeepAlive(total);
    }

    [Benchmark]
    public async Task Window_FixedSizeAsync()
    {
        var observed = 0;

        await foreach (var window in SuccessResultStreamAsync(CancellationToken.None).WindowAsync(WindowSize, CancellationToken.None).ConfigureAwait(false))
        {
            if (window.IsSuccess)
            {
                observed += window.Value.Count;
            }
        }

        GC.KeepAlive(observed);
    }

    [Benchmark]
    public async Task Window_WithFailuresAsync()
    {
        var observed = 0;

        await foreach (var window in MixedResultStreamAsync(CancellationToken.None).WindowAsync(WindowSize, CancellationToken.None).ConfigureAwait(false))
        {
            if (window.IsFailure)
            {
                observed++;
            }
        }

        GC.KeepAlive(observed);
    }

    [Benchmark]
    public async Task Partition_PredicateAsync()
    {
        var even = Channel.CreateBounded<Result<int>>(PartitionChannelCapacity);
        var odd = Channel.CreateBounded<Result<int>>(PartitionChannelCapacity);

        await SuccessResultStreamAsync(CancellationToken.None).PartitionAsync(static value => (value & 1) == 0, even.Writer, odd.Writer, CancellationToken.None).ConfigureAwait(false);

        var totals = 0;
        await foreach (var item in even.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (item.IsSuccess)
            {
                totals += item.Value;
            }
        }

        await foreach (var item in odd.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (item.IsSuccess)
            {
                totals += item.Value;
            }
        }

        GC.KeepAlive(totals);
    }

    [Benchmark]
    public async Task ForEach_FailureStopsAsync()
    {
        var result = await MixedResultStreamAsync(CancellationToken.None).ForEachAsync(ForEachCallbackAsync, CancellationToken.None).ConfigureAwait(false);
        GC.KeepAlive(result);
    }

    [Benchmark]
    public async Task ForEachLinkedCancellation_CallbackAsync()
    {
        var result = await SuccessResultStreamAsync(CancellationToken.None).ForEachLinkedCancellationAsync(ForEachLinkedCallbackAsync, CancellationToken.None).ConfigureAwait(false);
        GC.KeepAlive(result);
    }

    [Benchmark]
    public async Task TapSuccessEach_WithFailureAsync()
    {
        var result = await MixedResultStreamAsync(CancellationToken.None).TapSuccessEachAsync(TapSuccessAsync, CancellationToken.None).ConfigureAwait(false);
        GC.KeepAlive(result);
    }

    [Benchmark]
    public async Task TapSuccessEachAggregateErrors_AggregatesAsync()
    {
        var result = await MixedResultStreamAsync(CancellationToken.None).TapSuccessEachAggregateErrorsAsync(TapSuccessAsync, CancellationToken.None).ConfigureAwait(false);
        GC.KeepAlive(result);
    }

    [Benchmark]
    public async Task TapSuccessEachIgnoreErrors_IgnoresAsync()
    {
        var result = await MixedResultStreamAsync(CancellationToken.None).TapSuccessEachIgnoreErrorsAsync(TapSuccessAsync, CancellationToken.None).ConfigureAwait(false);
        GC.KeepAlive(result);
    }

    [Benchmark]
    public async Task TapFailureEach_WithFailuresAsync()
    {
        var result = await FailureResultStreamAsync(CancellationToken.None).TapFailureEachAsync(TapFailureAsync, CancellationToken.None).ConfigureAwait(false);
        GC.KeepAlive(result);
    }

    [Benchmark]
    public async Task TapFailureEachAggregateErrors_AggregatesAsync()
    {
        var result = await FailureResultStreamAsync(CancellationToken.None).TapFailureEachAggregateErrorsAsync(TapFailureAsync, CancellationToken.None).ConfigureAwait(false);
        GC.KeepAlive(result);
    }

    [Benchmark]
    public async Task TapFailureEachIgnoreErrors_IgnoresAsync()
    {
        var result = await FailureResultStreamAsync(CancellationToken.None).TapFailureEachIgnoreErrorsAsync(TapFailureAsync, CancellationToken.None).ConfigureAwait(false);
        GC.KeepAlive(result);
    }

    [Benchmark]
    public async Task CollectErrors_AggregateAsync()
    {
        var result = await MixedResultStreamAsync(CancellationToken.None).CollectErrorsAsync(CancellationToken.None).ConfigureAwait(false);
        GC.KeepAlive(result);
    }

    private Result<int>[] CreateResults(Func<int, Result<int>> selector)
    {
        var items = new Result<int>[ItemCount];
        for (var i = 0; i < items.Length; i++)
        {
            items[i] = selector(i);
        }

        return items;
    }

    private async IAsyncEnumerable<int> SuccessfulValuesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var value in _values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return value;
        }
    }

    private async IAsyncEnumerable<Result<int>> SuccessResultStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var result in _successResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return result;
        }
    }

    private async IAsyncEnumerable<Result<int>> MixedResultStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var result in _mixedResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return result;
        }
    }

    private async IAsyncEnumerable<Result<int>> FailureResultStreamAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        foreach (var result in _failureResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return result;
        }
    }

    private ValueTask<Result<int>> MapSelectorAsync(int value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<Result<int>>(Result.Ok(value + 1));
    }

    private ValueTask<Result<int>> MapSelectorWithFailureAsync(int value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (value == _mapFailureIndex)
        {
            return new ValueTask<Result<int>>(Result.Fail<int>(_failureError));
        }

        return new ValueTask<Result<int>>(Result.Ok(value + 1));
    }

    private static async IAsyncEnumerable<Result<int>> InnerStreamAsync(int seed, bool includeFailure, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.Yield();

        cancellationToken.ThrowIfCancellationRequested();
        yield return Result.Ok(seed);

        if (includeFailure)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return Result.Fail<int>(Error.From("inner-failure", ErrorCodes.Validation));
            yield break;
        }

        cancellationToken.ThrowIfCancellationRequested();
        yield return Result.Ok(seed + 1);
    }

    private IEnumerable<IAsyncEnumerable<Result<int>>> CreateFanInSources(CancellationToken cancellationToken)
    {
        for (var i = 0; i < FanInSourceCount; i++)
        {
            yield return i == 0 ? MixedResultStreamAsync(cancellationToken) : SuccessResultStreamAsync(cancellationToken);
        }
    }

    private static IReadOnlyList<ChannelWriter<Result<int>>> GetWriters(Channel<Result<int>>[] channels)
    {
        var writers = new ChannelWriter<Result<int>>[channels.Length];
        for (var i = 0; i < channels.Length; i++)
        {
            writers[i] = channels[i].Writer;
        }

        return writers;
    }

    private static ValueTask<Result<Unit>> ForEachCallbackAsync(Result<int> result, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (result.IsFailure)
        {
            return new ValueTask<Result<Unit>>(Result.Fail<Unit>(result.Error ?? Error.Unspecified()));
        }

        BenchmarkWorkloads.SimulateLightCpuWork();
        return new ValueTask<Result<Unit>>(Result.Ok(Unit.Value));
    }

    private static ValueTask<Result<Unit>> ForEachLinkedCallbackAsync(Result<int> result, CancellationToken linkedToken)
    {
        linkedToken.ThrowIfCancellationRequested();

        BenchmarkWorkloads.SimulateLightCpuWork();
        return new ValueTask<Result<Unit>>(result.IsFailure
            ? Result.Fail<Unit>(result.Error ?? Error.Unspecified())
            : Result.Ok(Unit.Value));
    }

    private static ValueTask TapSuccessAsync(int value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BenchmarkWorkloads.SimulateLightCpuWork();
        GC.KeepAlive(value);
        return ValueTask.CompletedTask;
    }

    private static ValueTask TapFailureAsync(Error error, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        BenchmarkWorkloads.SimulateLightCpuWork();
        GC.KeepAlive(error);
        return ValueTask.CompletedTask;
    }
}
