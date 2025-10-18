using System.Threading.Channels;

namespace Hugo;

/// <summary>
/// Represents an awaitable channel read with an associated continuation to execute when the read succeeds.
/// </summary>
public readonly struct ChannelCase
{
    private readonly Func<CancellationToken, Task<(bool HasValue, object? Value)>> _waiter;
    private readonly Func<object?, CancellationToken, Task<Result<Go.Unit>>> _continuation;

    private ChannelCase(
        Func<CancellationToken, Task<(bool HasValue, object? Value)>> waiter,
        Func<object?, CancellationToken, Task<Result<Go.Unit>>> continuation)
    {
        _waiter = waiter ?? throw new ArgumentNullException(nameof(waiter));
        _continuation = continuation ?? throw new ArgumentNullException(nameof(continuation));
    }

    internal Task<(bool HasValue, object? Value)> WaitAsync(CancellationToken cancellationToken) => _waiter(cancellationToken);

    internal Task<Result<Go.Unit>> ContinueWithAsync(object? value, CancellationToken cancellationToken) => _continuation(value, cancellationToken);

    /// <summary>
    /// Creates a channel case that executes <paramref name="onValue"/> when <paramref name="reader"/> produces a value.
    /// </summary>
    public static ChannelCase Create<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<Result<Go.Unit>>> onValue)
    {
        if (reader is null)
            throw new ArgumentNullException(nameof(reader));
        if (onValue is null)
            throw new ArgumentNullException(nameof(onValue));

        return new ChannelCase(
            async ct =>
            {
                try
                {
                    var value = await reader.ReadAsync(ct).ConfigureAwait(false);
                    return (true, (object?)value);
                }
                catch (ChannelClosedException)
                {
                    return (false, null);
                }
            },
            (value, ct) => onValue((T)value!, ct));
    }

    public static ChannelCase Create<T>(ChannelReader<T> reader, Func<T, Task<Result<Go.Unit>>> onValue)
    {
        if (onValue is null)
            throw new ArgumentNullException(nameof(onValue));

        return Create(reader, (item, _) => onValue(item));
    }

    public static ChannelCase Create<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task> onValue)
    {
        if (onValue is null)
            throw new ArgumentNullException(nameof(onValue));

        return Create(reader, async (item, ct) =>
        {
            await onValue(item, ct).ConfigureAwait(false);
            return Result.Ok(Go.Unit.Value);
        });
    }

    public static ChannelCase Create<T>(ChannelReader<T> reader, Action<T> onValue)
    {
        if (onValue is null)
            throw new ArgumentNullException(nameof(onValue));

        return Create(reader, (item, _) =>
        {
            onValue(item);
            return Task.FromResult(Result.Ok(Go.Unit.Value));
        });
    }
}
