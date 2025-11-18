using System.Threading.Channels;

namespace Hugo;

/// <summary>
/// Represents an awaitable channel read with an associated continuation to execute when the read succeeds.
/// </summary>
/// <typeparam name="TResult">The result type produced by the continuation associated with the case.</typeparam>
public readonly struct ChannelCase<TResult> : IEquatable<ChannelCase<TResult>>
{
    private readonly Func<CancellationToken, Task<(bool HasValue, object? Value)>> _waiter;
    private readonly Func<object?, CancellationToken, ValueTask<Result<TResult>>> _continuation;
    private readonly Func<(bool HasValue, object? Value)>? _readyProbe;

    internal ChannelCase(
        Func<CancellationToken, Task<(bool HasValue, object? Value)>> waiter,
        Func<object?, CancellationToken, ValueTask<Result<TResult>>> continuation,
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

    internal ValueTask<Result<TResult>> ContinueWithAsync(object? value, CancellationToken cancellationToken) => _continuation(value, cancellationToken);

    internal int Priority { get; }

    internal bool IsDefault { get; }

    internal ChannelCase<TResult> WithContinuation(Func<object?, CancellationToken, ValueTask<Result<TResult>>> continuation) =>
        new(_waiter, continuation ?? throw new ArgumentNullException(nameof(continuation)), _readyProbe, Priority, IsDefault);

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

    internal ChannelCase<TResult> WithPriority(int priority) => new(_waiter, _continuation, _readyProbe, priority, IsDefault);

    /// <summary>Creates a channel case using an asynchronous continuation that accepts the cancellation token.</summary>
    internal static ChannelCase<TResult> Create<T>(ChannelReader<T> reader, Func<T, CancellationToken, ValueTask<Result<TResult>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(onValue);

        return new ChannelCase<TResult>(
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
                T item;
                switch (state)
                {
                    case ImmediateRead<T> immediate:
                        item = immediate.Value;
                        break;
                    case DeferredRead<T> deferred:
                        if (deferred.Reader.TryRead(out T? immediateItem))
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
                            return Result.Fail<TResult>(Error.From("Channel closed before a value could be read.", ErrorCodes.SelectDrained));
                        }

                        break;
                    default:
                        return Result.Fail<TResult>(Error.From("Invalid channel state encountered during select continuation.", ErrorCodes.Exception));
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
                    return Result.Fail<TResult>(Error.FromException(ex));
                }
            },
            () =>
            {
                try
                {
                    ValueTask<bool> wait = reader.WaitToReadAsync(CancellationToken.None);
                    if (!wait.IsCompleted)
                    {
                        return (false, null);
                    }

                    if (!wait.Result)
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

    /// <summary>Creates a channel case using an asynchronous continuation that ignores the cancellation token.</summary>
    internal static ChannelCase<TResult> Create<T>(ChannelReader<T> reader, Func<T, ValueTask<Result<TResult>>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);
        return Create(reader, (value, _) => onValue(value));
    }

    /// <summary>Creates a channel case using a continuation that produces <typeparamref name="TResult"/>.</summary>
    internal static ChannelCase<TResult> Create<T>(ChannelReader<T> reader, Func<T, CancellationToken, ValueTask<TResult>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);
        return Create(reader, async (value, ct) => Result.Ok(await onValue(value, ct).ConfigureAwait(false)));
    }

    /// <summary>Creates a channel case using a continuation that produces <typeparamref name="TResult"/> without observing cancellation.</summary>
    internal static ChannelCase<TResult> Create<T>(ChannelReader<T> reader, Func<T, ValueTask<TResult>> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);
        return Create(reader, async (value, _) => Result.Ok(await onValue(value).ConfigureAwait(false)));
    }

    /// <summary>Creates a channel case using an asynchronous callback that does not produce a value.</summary>
    internal static ChannelCase<TResult> Create<T>(ChannelReader<T> reader, Func<T, CancellationToken, ValueTask> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);
        return Create(reader, async (value, ct) =>
        {
            await onValue(value, ct).ConfigureAwait(false);
            return Result.Ok(default(TResult)!);
        });
    }

    /// <summary>Creates a channel case using an asynchronous callback that ignores cancellation.</summary>
    internal static ChannelCase<TResult> Create<T>(ChannelReader<T> reader, Func<T, ValueTask> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);
        return Create(reader, async (value, _) =>
        {
            await onValue(value).ConfigureAwait(false);
            return Result.Ok(default(TResult)!);
        });
    }

    /// <summary>Creates a channel case using a synchronous callback.</summary>
    internal static ChannelCase<TResult> Create<T>(ChannelReader<T> reader, Action<T> onValue)
    {
        ArgumentNullException.ThrowIfNull(onValue);
        return Create(reader, (value, _) =>
        {
            onValue(value);
            return ValueTask.FromResult(Result.Ok(default(TResult)!));
        });
    }

    /// <summary>Creates the default case for a channel select loop.</summary>
    internal static ChannelCase<TResult> CreateDefault(Func<CancellationToken, ValueTask<Result<TResult>>> onDefault, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(onDefault);

        return new ChannelCase<TResult>(
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
                    return Result.Fail<TResult>(Error.FromException(ex));
                }
            },
            () => (true, (object?)null),
            priority,
            isDefault: true);
    }

    /// <summary>Creates the default case for a channel select loop without observing cancellation.</summary>
    internal static ChannelCase<TResult> CreateDefault(Func<ValueTask<Result<TResult>>> onDefault, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(onDefault);
        return CreateDefault(_ => onDefault(), priority);
    }

    /// <summary>Creates the default case for a channel select loop using a synchronous callback.</summary>
    internal static ChannelCase<TResult> CreateDefault(Func<Result<TResult>> onDefault, int priority = 0)
    {
        ArgumentNullException.ThrowIfNull(onDefault);
        return CreateDefault(_ => ValueTask.FromResult(onDefault()), priority);
    }

    public override bool Equals(object? obj) => obj is ChannelCase<TResult> other && Equals(other);

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

    public static bool operator ==(ChannelCase<TResult> left, ChannelCase<TResult> right) => left.Equals(right);

    public static bool operator !=(ChannelCase<TResult> left, ChannelCase<TResult> right) => !left.Equals(right);

    public bool Equals(ChannelCase<TResult> other) => ReferenceEquals(_waiter, other._waiter)
        && ReferenceEquals(_continuation, other._continuation)
        && ReferenceEquals(_readyProbe, other._readyProbe)
        && Priority == other.Priority
        && IsDefault == other.IsDefault;
}

/// <summary>
/// Provides non-generic factory helpers for composing <see cref=ChannelCase{TResult}/> instances.
/// </summary>
public static class ChannelCase
{
    /// <summary>Creates a channel case using an asynchronous continuation that accepts the cancellation token.</summary>
    public static ChannelCase<TResult> Create<T, TResult>(ChannelReader<T> reader, Func<T, CancellationToken, ValueTask<Result<TResult>>> onValue) =>
        ChannelCase<TResult>.Create(reader, onValue);

    /// <summary>Creates a channel case using an asynchronous continuation that ignores the cancellation token.</summary>
    public static ChannelCase<TResult> Create<T, TResult>(ChannelReader<T> reader, Func<T, ValueTask<Result<TResult>>> onValue) =>
        ChannelCase<TResult>.Create(reader, onValue);

    /// <summary>Creates a channel case using a continuation that produces <typeparamref name="TResult"/>.</summary>
    public static ChannelCase<TResult> Create<T, TResult>(ChannelReader<T> reader, Func<T, CancellationToken, ValueTask<TResult>> onValue) =>
        ChannelCase<TResult>.Create(reader, onValue);

    /// <summary>Creates a channel case using an asynchronous continuation that produces <typeparamref name="TResult"/>.</summary>
    public static ChannelCase<TResult> Create<T, TResult>(ChannelReader<T> reader, Func<T, ValueTask<TResult>> onValue) =>
        ChannelCase<TResult>.Create(reader, onValue);

    /// <summary>Creates a channel case using an asynchronous continuation that observes the cancellation token but returns no result.</summary>
    public static ChannelCase<TResult> Create<T, TResult>(ChannelReader<T> reader, Func<T, CancellationToken, ValueTask> onValue) =>
        ChannelCase<TResult>.Create(reader, onValue);

    /// <summary>Creates a channel case using an asynchronous continuation that ignores the cancellation token and returns no result.</summary>
    public static ChannelCase<TResult> Create<T, TResult>(ChannelReader<T> reader, Func<T, ValueTask> onValue) =>
        ChannelCase<TResult>.Create(reader, onValue);

    /// <summary>Creates a channel case using a synchronous callback.</summary>
    public static ChannelCase<TResult> Create<T, TResult>(ChannelReader<T> reader, Action<T> onValue) =>
        ChannelCase<TResult>.Create(reader, onValue);

    /// <summary>Creates the default case for a channel select loop.</summary>
    public static ChannelCase<TResult> CreateDefault<TResult>(Func<CancellationToken, ValueTask<Result<TResult>>> onDefault, int priority = 0) =>
        ChannelCase<TResult>.CreateDefault(onDefault, priority);

    /// <summary>Creates the default case for a channel select loop without observing cancellation.</summary>
    public static ChannelCase<TResult> CreateDefault<TResult>(Func<ValueTask<Result<TResult>>> onDefault, int priority = 0) =>
        ChannelCase<TResult>.CreateDefault(onDefault, priority);

    /// <summary>Creates the default case for a channel select loop using a synchronous callback.</summary>
    public static ChannelCase<TResult> CreateDefault<TResult>(Func<Result<TResult>> onDefault, int priority = 0) =>
        ChannelCase<TResult>.CreateDefault(onDefault, priority);
}

internal sealed class DeferredRead<T>(ChannelReader<T> reader)
{
    public ChannelReader<T> Reader { get; } = reader ?? throw new ArgumentNullException(nameof(reader));
}

internal sealed class ImmediateRead<T>(T value)
{
    public T Value { get; } = value;
}

/// <summary>
/// Represents a reusable template for creating typed <see cref="ChannelCase{TResult}"/> instances.
/// </summary>
/// <typeparam name="T">The type of data read from the channel.</typeparam>
/// <param name="reader">The channel reader that drives the template.</param>
public readonly struct ChannelCaseTemplate<T>(ChannelReader<T> reader) : IEquatable<ChannelCaseTemplate<T>>
{
    public ChannelReader<T> Reader { get; } = reader ?? throw new ArgumentNullException(nameof(reader));

    public ChannelCase<TResult> With<TResult>(Func<T, CancellationToken, ValueTask<Result<TResult>>> onValue) => ChannelCase.Create<T, TResult>(Reader, onValue);

    public ChannelCase<TResult> With<TResult>(Func<T, ValueTask<Result<TResult>>> onValue) => ChannelCase.Create<T, TResult>(Reader, onValue);

    public ChannelCase<TResult> With<TResult>(Func<T, CancellationToken, ValueTask<TResult>> onValue) => ChannelCase.Create<T, TResult>(Reader, onValue);

    public ChannelCase<TResult> With<TResult>(Func<T, ValueTask<TResult>> onValue) => ChannelCase.Create<T, TResult>(Reader, onValue);

    public ChannelCase<TResult> With<TResult>(Func<T, CancellationToken, ValueTask> onValue) => ChannelCase.Create<T, TResult>(Reader, onValue);

    public ChannelCase<TResult> With<TResult>(Func<T, ValueTask> onValue) => ChannelCase.Create<T, TResult>(Reader, onValue);

    public ChannelCase<TResult> With<TResult>(Action<T> onValue) => ChannelCase.Create<T, TResult>(Reader, onValue);

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
    public static ChannelCaseTemplate<T> From<T>(ChannelReader<T> reader) => new(reader);

    extension<T>(IEnumerable<ChannelCaseTemplate<T>> templates)
    {
        public ChannelCase<TResult>[] With<TResult>(Func<T, CancellationToken, ValueTask<Result<TResult>>> onValue)
        {
            ArgumentNullException.ThrowIfNull(templates);
            ArgumentNullException.ThrowIfNull(onValue);
            return Materialize(templates, template => template.With(onValue));
        }

        public ChannelCase<TResult>[] With<TResult>(Func<T, ValueTask<Result<TResult>>> onValue)
        {
            ArgumentNullException.ThrowIfNull(templates);
            ArgumentNullException.ThrowIfNull(onValue);
            return Materialize(templates, template => template.With(onValue));
        }

        public ChannelCase<TResult>[] With<TResult>(Func<T, CancellationToken, ValueTask<TResult>> onValue)
        {
            ArgumentNullException.ThrowIfNull(templates);
            ArgumentNullException.ThrowIfNull(onValue);
            return Materialize(templates, template => template.With(onValue));
        }

        public ChannelCase<TResult>[] With<TResult>(Func<T, ValueTask<TResult>> onValue)
        {
            ArgumentNullException.ThrowIfNull(templates);
            ArgumentNullException.ThrowIfNull(onValue);
            return Materialize(templates, template => template.With(onValue));
        }

        public ChannelCase<TResult>[] With<TResult>(Func<T, CancellationToken, ValueTask> onValue)
        {
            ArgumentNullException.ThrowIfNull(templates);
            ArgumentNullException.ThrowIfNull(onValue);
            return Materialize(templates, template => template.With<TResult>(onValue));
        }

        public ChannelCase<TResult>[] With<TResult>(Func<T, ValueTask> onValue)
        {
            ArgumentNullException.ThrowIfNull(templates);
            ArgumentNullException.ThrowIfNull(onValue);
            return Materialize(templates, template => template.With<TResult>(onValue));
        }

        public ChannelCase<TResult>[] With<TResult>(Action<T> onValue)
        {
            ArgumentNullException.ThrowIfNull(templates);
            ArgumentNullException.ThrowIfNull(onValue);
            return Materialize(templates, template => template.With<TResult>(onValue));
        }
    }

    private static ChannelCase<TResult>[] Materialize<T, TResult>(IEnumerable<ChannelCaseTemplate<T>> templates, Func<ChannelCaseTemplate<T>, ChannelCase<TResult>> projector)
    {
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(projector);

        var list = templates is ICollection<ChannelCaseTemplate<T>> collection
            ? new List<ChannelCase<TResult>>(collection.Count)
            : new List<ChannelCase<TResult>>();
        list.AddRange(templates.Select(projector));

        return [.. list];
    }
}
