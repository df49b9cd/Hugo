using System.Collections.Concurrent;

namespace Hugo;

/// <summary>
/// A concurrency-safe pool of reusable objects, similar to Go's <c>sync.Pool</c>.
/// </summary>
public sealed class Pool<T>
    where T : class
{
    private readonly ConcurrentBag<T> _items = [];

    /// <summary>Gets or sets the factory used to create new instances when the pool is empty.</summary>
    public Func<T>? New { get; init; }

    /// <summary>Retrieves an item from the pool, creating one via <see cref="New"/> if necessary.</summary>
    /// <returns>An item from the pool.</returns>
    public T Get() => _items.TryTake(out var item)
        ? item
        : New?.Invoke() ?? throw new InvalidOperationException("The 'New' factory function must be set before Get can be called on an empty pool.");

    /// <summary>Returns an item to the pool.</summary>
    /// <param name="item">The item to return.</param>
    public void Put(T item)
    {
        ArgumentNullException.ThrowIfNull(item);

        _items.Add(item);
    }
}
