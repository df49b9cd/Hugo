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
        ArgumentNullException.ThrowIfNull(reader);

        return onValue is null
            ? throw new ArgumentNullException(nameof(onValue))
            : new ChannelCase(
            async ct =>
            {
                try
                {
                    if (!await reader.WaitToReadAsync(ct).ConfigureAwait(false))
                    {
                        return (false, null);
                    }

                    return (true, new DeferredRead<T>(reader));
                }
                catch (ChannelClosedException)
                {
                    return (false, null);
                }
            },
            async (state, ct) =>
            {
                var deferred = (DeferredRead<T>)state!;
                if (!deferred.Reader.TryRead(out var item))
                {
                    try
                    {
                        item = await deferred.Reader.ReadAsync(ct).ConfigureAwait(false);
                    }
                    catch (ChannelClosedException)
                    {
                        return Result.Fail<Go.Unit>(Error.From("Channel closed before a value could be read.", ErrorCodes.SelectDrained));
                    }
                }

                try
                {
                    return await onValue(item, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Result.Fail<Go.Unit>(Error.FromException(ex));
                }
            });
    }

    public static ChannelCase Create<T>(ChannelReader<T> reader, Func<T, Task<Result<Go.Unit>>> onValue) => onValue is null ? throw new ArgumentNullException(nameof(onValue)) : Create(reader, (item, _) => onValue(item));

    public static ChannelCase Create<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task> onValue) => onValue is null
            ? throw new ArgumentNullException(nameof(onValue))
            : Create(reader, async (item, ct) =>
        {
            await onValue(item, ct).ConfigureAwait(false);
            return Result.Ok(Go.Unit.Value);
        });

    public static ChannelCase Create<T>(ChannelReader<T> reader, Action<T> onValue) => onValue is null
            ? throw new ArgumentNullException(nameof(onValue))
            : Create(reader, (item, _) =>
        {
            onValue(item);
            return Task.FromResult(Result.Ok(Go.Unit.Value));
        });
}

internal sealed class DeferredRead<T>(ChannelReader<T> reader)
{
    public ChannelReader<T> Reader { get; } = reader ?? throw new ArgumentNullException(nameof(reader));
}

/// <summary>
/// Represents a reusable template for creating typed <see cref="ChannelCase"/> instances.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ChannelCaseTemplate{T}"/> struct.
/// </remarks>
/// <param name="reader">The channel reader that drives the template.</param>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> is <see langword="null"/>.</exception>
public readonly struct ChannelCaseTemplate<T>(ChannelReader<T> reader)
{

    /// <summary>
    /// Gets the channel reader associated with this template.
    /// </summary>
    public ChannelReader<T> Reader { get; } = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>
    /// Creates a <see cref="ChannelCase"/> using the supplied continuation.
    /// </summary>
    public ChannelCase With(Func<T, CancellationToken, Task<Result<Go.Unit>>> onValue) => ChannelCase.Create(Reader, onValue);

    /// <summary>
    /// Creates a <see cref="ChannelCase"/> using the supplied continuation without a cancellation token parameter.
    /// </summary>
    public ChannelCase With(Func<T, Task<Result<Go.Unit>>> onValue) => ChannelCase.Create(Reader, onValue);

    /// <summary>
    /// Creates a <see cref="ChannelCase"/> using the supplied continuation that returns a <see cref="Task"/>.
    /// </summary>
    public ChannelCase With(Func<T, CancellationToken, Task> onValue) => ChannelCase.Create(Reader, onValue);

    /// <summary>
    /// Creates a <see cref="ChannelCase"/> using the supplied synchronous callback.
    /// </summary>
    public ChannelCase With(Action<T> onValue) => ChannelCase.Create(Reader, onValue);
}

/// <summary>
/// Provides helpers for composing and materializing groups of <see cref="ChannelCaseTemplate{T}"/> instances.
/// </summary>
public static class ChannelCaseTemplates
{
    /// <summary>
    /// Creates a template backed by the specified reader.
    /// </summary>
    public static ChannelCaseTemplate<T> From<T>(ChannelReader<T> reader) => new(reader);

    /// <summary>
    /// Materializes the supplied templates with the provided continuation.
    /// </summary>
    public static ChannelCase[] With<T>(this IEnumerable<ChannelCaseTemplate<T>> templates, Func<T, CancellationToken, Task<Result<Go.Unit>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(templates);

        ArgumentNullException.ThrowIfNull(onValue);

        return Materialize(templates, template => template.With(onValue));
    }

    /// <summary>
    /// Materializes the supplied templates with the provided continuation.
    /// </summary>
    public static ChannelCase[] With<T>(this IEnumerable<ChannelCaseTemplate<T>> templates, Func<T, Task<Result<Go.Unit>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(templates);

        ArgumentNullException.ThrowIfNull(onValue);

        return Materialize(templates, template => template.With(onValue));
    }

    /// <summary>
    /// Materializes the supplied templates with the provided continuation.
    /// </summary>
    public static ChannelCase[] With<T>(this IEnumerable<ChannelCaseTemplate<T>> templates, Func<T, CancellationToken, Task> onValue)
    {
        ArgumentNullException.ThrowIfNull(templates);

        ArgumentNullException.ThrowIfNull(onValue);

        return Materialize(templates, template => template.With(onValue));
    }

    /// <summary>
    /// Materializes the supplied templates with the provided callback.
    /// </summary>
    public static ChannelCase[] With<T>(this IEnumerable<ChannelCaseTemplate<T>> templates, Action<T> onValue)
    {
        ArgumentNullException.ThrowIfNull(templates);

        ArgumentNullException.ThrowIfNull(onValue);

        return Materialize(templates, template => template.With(onValue));
    }

    private static ChannelCase[] Materialize<T>(IEnumerable<ChannelCaseTemplate<T>> templates, Func<ChannelCaseTemplate<T>, ChannelCase> projector)
    {
        var list = templates is ICollection<ChannelCaseTemplate<T>> collection
            ? new List<ChannelCase>(collection.Count)
            : [];

        foreach (var template in templates)
        {
            list.Add(projector(template));
        }

        return [.. list];
    }
}
