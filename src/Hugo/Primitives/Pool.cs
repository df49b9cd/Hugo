using System;
using System.Collections.Concurrent;

namespace Hugo;

/// <summary>
/// A concurrency-safe pool of reusable objects, similar to Go's <c>sync.Pool</c>.
/// </summary>
public sealed class Pool<T>
    where T : class
{
    private readonly ConcurrentBag<T> _items = [];

    public Func<T>? New { get; init; }

    public T Get() => _items.TryTake(out var item)
        ? item
        : New?.Invoke() ?? throw new InvalidOperationException("The 'New' factory function must be set before Get can be called on an empty pool.");

    public void Put(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        _items.Add(item);
    }
}
