using System;
using System.Threading.Channels;

namespace Hugo;

/// <summary>
/// Represents an awaitable channel read with an associated continuation to execute when the read succeeds.
/// </summary>
public readonly struct ChannelCase : IEquatable<ChannelCase>
{
    private readonly Func<CancellationToken, Task<(bool HasValue, object? Value)>> _waiter;
    private readonly Func<object?, CancellationToken, Task<Result<Go.Unit>>> _continuation;
    private readonly Func<(bool HasValue, object? Value)>? _readyProbe;

    private ChannelCase(
        Func<CancellationToken, Task<(bool HasValue, object? Value)>> waiter,
        Func<object?, CancellationToken, Task<Result<Go.Unit>>> continuation,
        Func<(bool HasValue, object? Value)>? readyProbe,
        int priority,
        bool isDefault)
    {
        _waiter = waiter ?? throw new ArgumentNullException(nameof(waiter));
        _continuation = continuation ?? throw new ArgumentNullException(nameof(continuation));
        _readyProbe = readyProbe;
        Priority = priority;
        IsDefault = isDefault;
    }

    internal Task<(bool HasValue, object? Value)> WaitAsync(CancellationToken cancellationToken) => _waiter(cancellationToken);

    internal Task<Result<Go.Unit>> ContinueWithAsync(object? value, CancellationToken cancellationToken) => _continuation(value, cancellationToken);

    internal int Priority { get; }

    internal bool IsDefault { get; }

    internal bool TryDequeueImmediately(out object? state)
    {
        if (_readyProbe is null)
        {
            state = null;
            return false;
        }

        var (hasValue, value) = _readyProbe();
        state = value;
        return hasValue;
    }

    internal ChannelCase WithPriority(int priority) => new(_waiter, _continuation, _readyProbe, priority, IsDefault);

    /// <summary>Creates a channel case that executes <paramref name="onValue"/> when <paramref name="reader"/> produces a value.</summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="reader">The source reader to observe.</param>
    /// <param name="onValue">The continuation invoked when a value is available.</param>
    /// <returns>A configured <see cref="ChannelCase"/>.</returns>
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
                T item = default!;
                switch (state)
                {
                    case ImmediateRead<T> immediate:
                        item = immediate.Value;
                        break;
                    case DeferredRead<T> deferred:
                        if (deferred.Reader.TryRead(out var immediateItem))
                        {
                            item = immediateItem;
                            break;
                        }

                        try
                        {
                            item = await deferred.Reader.ReadAsync(ct).ConfigureAwait(false);
                        }
                        catch (ChannelClosedException)
                        {
                            return Result.Fail<Go.Unit>(Error.From("Channel closed before a value could be read.", ErrorCodes.SelectDrained));
                        }

                        break;
                    default:
                        return Result.Fail<Go.Unit>(Error.From("Invalid channel state encountered during select continuation.", ErrorCodes.Exception));
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
            },
            () =>
            {
                try
                {
                    var wait = reader.WaitToReadAsync(CancellationToken.None);
                    if (!wait.IsCompleted)
                    {
                        return (false, null);
                    }

                    if (!wait.GetAwaiter().GetResult())
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
            priority: 0,
            isDefault: false);
    }

    /// <summary>Creates a channel case using a continuation that does not require a cancellation token.</summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="reader">The source reader to observe.</param>
    /// <param name="onValue">The continuation invoked when a value is available.</param>
    /// <returns>A configured <see cref="ChannelCase"/>.</returns>
    public static ChannelCase Create<T>(ChannelReader<T> reader, Func<T, Task<Result<Go.Unit>>> onValue) => onValue is null ? throw new ArgumentNullException(nameof(onValue)) : Create(reader, (item, _) => onValue(item));

    /// <summary>Creates a channel case that runs a task-returning continuation.</summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="reader">The source reader to observe.</param>
    /// <param name="onValue">The continuation invoked when a value is available.</param>
    /// <returns>A configured <see cref="ChannelCase"/>.</returns>
    public static ChannelCase Create<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task> onValue) => onValue is null
            ? throw new ArgumentNullException(nameof(onValue))
            : Create(reader, async (item, ct) =>
        {
            await onValue(item, ct).ConfigureAwait(false);
            return Result.Ok(Go.Unit.Value);
        });

    /// <summary>Creates a channel case that runs a synchronous callback.</summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="reader">The source reader to observe.</param>
    /// <param name="onValue">The callback invoked when a value is available.</param>
    /// <returns>A configured <see cref="ChannelCase"/>.</returns>
    public static ChannelCase Create<T>(ChannelReader<T> reader, Action<T> onValue) => onValue is null
            ? throw new ArgumentNullException(nameof(onValue))
            : Create(reader, (item, _) =>
        {
            onValue(item);
            return Task.FromResult(Result.Ok(Go.Unit.Value));
        });

    /// <summary>Creates the default case for a channel select loop.</summary>
    /// <param name="onDefault">The continuation invoked when no channel is ready.</param>
    /// <param name="priority">The priority applied when scheduling the default case.</param>
    /// <returns>A configured <see cref="ChannelCase"/> representing the default branch.</returns>
    public static ChannelCase CreateDefault(Func<CancellationToken, Task<Result<Go.Unit>>> onDefault, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return new ChannelCase(
            _ => Task.FromResult((true, (object?)null)),
            async (_, ct) =>
            {
                try
                {
                    return await onDefault(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Result.Fail<Go.Unit>(Error.FromException(ex));
                }
            },
            () => (true, (object?)null),
            priority,
            isDefault: true);
    }

    /// <summary>Creates the default case for a channel select loop without using a cancellation token.</summary>
    /// <param name="onDefault">The continuation invoked when no channel is ready.</param>
    /// <param name="priority">The priority applied when scheduling the default case.</param>
    /// <returns>A configured <see cref="ChannelCase"/> representing the default branch.</returns>
    public static ChannelCase CreateDefault(Func<Task<Result<Go.Unit>>> onDefault, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return CreateDefault(_ => onDefault(), priority);
    }

    /// <summary>Creates the default case for a channel select loop using a synchronous callback.</summary>
    /// <param name="onDefault">The callback invoked when no channel is ready.</param>
    /// <param name="priority">The priority applied when scheduling the default case.</param>
    /// <returns>A configured <see cref="ChannelCase"/> representing the default branch.</returns>
    public static ChannelCase CreateDefault(Func<Result<Go.Unit>> onDefault, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return CreateDefault(_ => Task.FromResult(onDefault()), priority);
    }

    public override bool Equals(object? obj) => obj is ChannelCase other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(_waiter);
        hash.Add(_continuation);
        hash.Add(_readyProbe);
        hash.Add(Priority);
        hash.Add(IsDefault);
        return hash.ToHashCode();
    }

    public static bool operator ==(ChannelCase left, ChannelCase right) => left.Equals(right);

    public static bool operator !=(ChannelCase left, ChannelCase right) => !left.Equals(right);

    public bool Equals(ChannelCase other) => ReferenceEquals(_waiter, other._waiter)
        && ReferenceEquals(_continuation, other._continuation)
        && ReferenceEquals(_readyProbe, other._readyProbe)
        && Priority == other.Priority
        && IsDefault == other.IsDefault;
}

internal sealed class DeferredRead<T>(ChannelReader<T> reader)
{
    public ChannelReader<T> Reader { get; } = reader ?? throw new ArgumentNullException(nameof(reader));
}

internal abstract class ImmediateRead<T>(T value)
{
    public T Value { get; } = value;
}

/// <summary>
/// Represents a reusable template for creating typed <see cref="ChannelCase"/> instances.
/// </summary>
/// <remarks>
/// Initializes a new instance of the <see cref="ChannelCaseTemplate{T}"/> struct.
/// </remarks>
/// <param name="reader">The channel reader that drives the template.</param>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="reader"/> is <see langword="null"/>.</exception>
public readonly struct ChannelCaseTemplate<T>(ChannelReader<T> reader) : IEquatable<ChannelCaseTemplate<T>>
{

    /// <summary>
    /// Gets the channel reader associated with this template.
    /// </summary>
    public ChannelReader<T> Reader { get; } = reader ?? throw new ArgumentNullException(nameof(reader));

    /// <summary>
    /// Creates a <see cref="ChannelCase"/> using the supplied continuation.
    /// </summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="onValue">The continuation invoked when a value is available.</param>
    /// <returns>A configured <see cref="ChannelCase"/>.</returns>
    public ChannelCase With(Func<T, CancellationToken, Task<Result<Go.Unit>>> onValue) => ChannelCase.Create(Reader, onValue);

    /// <summary>
    /// Creates a <see cref="ChannelCase"/> using the supplied continuation without a cancellation token parameter.
    /// </summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="onValue">The continuation invoked when a value is available.</param>
    /// <returns>A configured <see cref="ChannelCase"/>.</returns>
    public ChannelCase With(Func<T, Task<Result<Go.Unit>>> onValue) => ChannelCase.Create(Reader, onValue);

    /// <summary>
    /// Creates a <see cref="ChannelCase"/> using the supplied continuation that returns a <see cref="Task"/>.
    /// </summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="onValue">The continuation invoked when a value is available.</param>
    /// <returns>A configured <see cref="ChannelCase"/>.</returns>
    public ChannelCase With(Func<T, CancellationToken, Task> onValue) => ChannelCase.Create(Reader, onValue);

    /// <summary>
    /// Creates a <see cref="ChannelCase"/> using the supplied synchronous callback.
    /// </summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="onValue">The callback invoked when a value is available.</param>
    /// <returns>A configured <see cref="ChannelCase"/>.</returns>
    public ChannelCase With(Action<T> onValue) => ChannelCase.Create(Reader, onValue);

    public override bool Equals(object? obj) => obj is ChannelCaseTemplate<T> other && Equals(other);

    public override int GetHashCode() => Reader.GetHashCode();

    public static bool operator ==(ChannelCaseTemplate<T> left, ChannelCaseTemplate<T> right) => left.Equals(right);

    public static bool operator !=(ChannelCaseTemplate<T> left, ChannelCaseTemplate<T> right) => !left.Equals(right);

    public bool Equals(ChannelCaseTemplate<T> other) => ReferenceEquals(Reader, other.Reader);
}

/// <summary>
/// Provides helpers for composing and materializing groups of <see cref="ChannelCaseTemplate{T}"/> instances.
/// </summary>
public static class ChannelCaseTemplates
{
    /// <summary>Creates a template backed by the specified reader.</summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="reader">The channel reader to wrap.</param>
    /// <returns>A reusable channel case template.</returns>
    public static ChannelCaseTemplate<T> From<T>(ChannelReader<T> reader) => new(reader);

    /// <summary>
    /// Materializes the supplied templates with the provided continuation.
    /// </summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="templates">The templates to materialize.</param>
    /// <param name="onValue">The continuation invoked when a value is available.</param>
    /// <returns>An array of materialized channel cases.</returns>
    public static ChannelCase[] With<T>(this IEnumerable<ChannelCaseTemplate<T>> templates, Func<T, CancellationToken, Task<Result<Go.Unit>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(templates);

        ArgumentNullException.ThrowIfNull(onValue);

        return Materialize(templates, template => template.With(onValue));
    }

    /// <summary>
    /// Materializes the supplied templates with the provided continuation.
    /// </summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="templates">The templates to materialize.</param>
    /// <param name="onValue">The continuation invoked when a value is available.</param>
    /// <returns>An array of materialized channel cases.</returns>
    public static ChannelCase[] With<T>(this IEnumerable<ChannelCaseTemplate<T>> templates, Func<T, Task<Result<Go.Unit>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(templates);

        ArgumentNullException.ThrowIfNull(onValue);

        return Materialize(templates, template => template.With(onValue));
    }

    /// <summary>
    /// Materializes the supplied templates with the provided continuation.
    /// </summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="templates">The templates to materialize.</param>
    /// <param name="onValue">The continuation invoked when a value is available.</param>
    /// <returns>An array of materialized channel cases.</returns>
    public static ChannelCase[] With<T>(this IEnumerable<ChannelCaseTemplate<T>> templates, Func<T, CancellationToken, Task> onValue)
    {
        ArgumentNullException.ThrowIfNull(templates);

        ArgumentNullException.ThrowIfNull(onValue);

        return Materialize(templates, template => template.With(onValue));
    }

    /// <summary>
    /// Materializes the supplied templates with the provided callback.
    /// </summary>
    /// <typeparam name="T">The type of data read from the channel.</typeparam>
    /// <param name="templates">The templates to materialize.</param>
    /// <param name="onValue">The callback invoked when a value is available.</param>
    /// <returns>An array of materialized channel cases.</returns>
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
        list.AddRange(templates.Select(projector));

        return [.. list];
    }
}
