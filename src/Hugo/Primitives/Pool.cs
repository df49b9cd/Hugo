using System.Collections.Concurrent;

namespace Hugo;

/// <summary>
/// A concurrency-safe pool of reusable objects, similar to Go's <c>sync.Pool</c>.
/// </summary>
public sealed class Pool<T>
    where T : class
{
    private readonly ConcurrentBag<T> _items = [];
    private int _count;

    /// <summary>Gets or sets the factory used to create new instances when the pool is empty.</summary>
    public Func<T>? New { get; init; }

    /// <summary>Optional upper bound on retained items; when set, items beyond the limit are dropped on return.</summary>
    public int? MaxCapacity { get; init; }

    /// <summary>Gets the current approximate number of items held by the pool.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Retrieves an item from the pool, creating one via <see cref="New"/> if necessary.</summary>
    /// <returns>An item from the pool.</returns>
    public T Get()
    {
        if (_items.TryTake(out var item))
        {
            Interlocked.Decrement(ref _count);
            return item;
        }

        return New?.Invoke() ?? throw new InvalidOperationException("The 'New' factory function must be set before Get can be called on an empty pool.");
    }

    /// <summary>Returns an item to the pool.</summary>
    /// <param name="item">The item to return.</param>
    public void Put(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        var newCount = Interlocked.Increment(ref _count);
        if (MaxCapacity is { } max && newCount > max)
        {
            Interlocked.Decrement(ref _count);
            return; // drop overflow to avoid unbounded growth per perf guidelines.
        }

        _items.Add(item);
    }

    /// <summary>Removes all items from the pool and returns the number of items cleared.</summary>
    public int Clear()
    {
        var removed = 0;
        while (_items.TryTake(out _))
        {
            removed++;
        }

        if (removed > 0)
        {
            Interlocked.Add(ref _count, -removed);
        }

        return removed;
    }
}
