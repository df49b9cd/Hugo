using System.Collections.Concurrent;
using BenchmarkDotNet.Attributes;
using Hugo;
using Microsoft.Extensions.ObjectPool;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
public class PoolBenchmarks
{
    [Params(128, 1024)]
    public int OperationCount { get; set; }

    private Pool<object>? _hugoPool;
    private DefaultObjectPool<object>? _defaultPool;

    [GlobalSetup]
    public void Setup()
    {
        _hugoPool = new Pool<object> { New = static () => new object() };
        _defaultPool = new DefaultObjectPool<object>(new DefaultPooledObjectPolicy<object>(), maximumRetained: Math.Max(32, OperationCount));
    }

    [Benchmark]
    public void HugoPoolRoundTrip()
    {
        var pool = _hugoPool ?? throw new InvalidOperationException();
        var buffer = new object[OperationCount];

        for (var i = 0; i < OperationCount; i++)
        {
            buffer[i] = pool.Get();
        }

        for (var i = 0; i < OperationCount; i++)
        {
            pool.Put(buffer[i]);
        }
    }

    [Benchmark]
    public void DefaultObjectPoolRoundTrip()
    {
        var pool = _defaultPool ?? throw new InvalidOperationException();
        var buffer = new object[OperationCount];

        for (var i = 0; i < OperationCount; i++)
        {
            buffer[i] = pool.Get();
        }

        for (var i = 0; i < OperationCount; i++)
        {
            pool.Return((object)buffer[i]);
        }
    }

    [Benchmark]
    public void ConcurrentBagRoundTrip()
    {
        var bag = new ConcurrentBag<object>();
        for (var i = 0; i < OperationCount; i++)
        {
            bag.Add(new object());
        }

        var buffer = new object[OperationCount];
        for (var i = 0; i < OperationCount; i++)
        {
            if (!bag.TryTake(out var item))
            {
                item = new object();
            }

            buffer[i] = item;
        }

        for (var i = 0; i < OperationCount; i++)
        {
            bag.Add(buffer[i]);
        }

        GC.KeepAlive(bag);
    }
}
