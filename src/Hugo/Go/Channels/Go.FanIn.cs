using System.Threading.Channels;

namespace Hugo;

/// <content>
/// Provides fan-in helpers for aggregating channel data.
/// </content>
public static partial class Go
{
    /// <summary>
    /// Drains the provided channel readers until each one completes, invoking <paramref name="onValue"/> for every observed value.
    /// </summary>
    public static Task<Result<Unit>> SelectFanInAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, CancellationToken, Task<Result<Unit>>> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readers);
        ArgumentNullException.ThrowIfNull(onValue);

        ChannelReader<T>[] collected = GoChannelHelpers.CollectSources(readers);
        if (collected.Length == 0)
        {
            return Task.FromResult(Result.Ok(Unit.Value));
        }

        for (int i = 0; i < collected.Length; i++)
        {
            if (collected[i] is null)
            {
                throw new ArgumentException("Reader collection cannot contain null entries.", nameof(readers));
            }
        }

        TimeSpan effectiveTimeout = timeout ?? Timeout.InfiniteTimeSpan;
        return GoChannelHelpers.SelectFanInAsyncCore(collected, onValue, effectiveTimeout, provider, cancellationToken);
    }

    public static Task<Result<Unit>> SelectFanInAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, Task<Result<Unit>>> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return SelectFanInAsync(readers, (value, _) => onValue(value), timeout, provider, cancellationToken);
    }

    public static Task<Result<Unit>> SelectFanInAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, CancellationToken, Task> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return SelectFanInAsync(readers, async (value, ct) =>
        {
            await onValue(value, ct).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }, timeout, provider, cancellationToken);
    }

    public static Task<Result<Unit>> SelectFanInAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, Task> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return SelectFanInAsync(readers, async (value, _) =>
        {
            await onValue(value).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }, timeout, provider, cancellationToken);
    }

    public static Task<Result<Unit>> SelectFanInAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Action<T> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return SelectFanInAsync(readers, (value, _) =>
        {
            onValue(value);
            return Task.FromResult(Result.Ok(Unit.Value));
        }, timeout, provider, cancellationToken);
    }

    /// <summary>
    /// Fans multiple source channels into a destination writer, optionally completing the writer when the sources finish.
    /// </summary>
    public static Task<Result<Unit>> FanInAsync<T>(
        IEnumerable<ChannelReader<T>> sources,
        ChannelWriter<T> destination,
        bool completeDestination = true,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(destination);

        ChannelReader<T>[] readers = GoChannelHelpers.CollectSources(sources);
        if (readers.Length != 0)
        {
            for (int i = 0; i < readers.Length; i++)
            {
                if (readers[i] is null)
                {
                    throw new ArgumentException("Source readers cannot contain null entries.", nameof(sources));
                }
            }

            return GoChannelHelpers.FanInAsyncCore(readers, destination, completeDestination, timeout ?? Timeout.InfiniteTimeSpan, provider, cancellationToken);
        }

        if (completeDestination)
        {
            destination.TryComplete();
        }

        return Task.FromResult(Result.Ok(Unit.Value));
    }

    /// <summary>
    /// Fans multiple source channels into a newly created channel.
    /// </summary>
    public static ChannelReader<T> FanIn<T>(
        IEnumerable<ChannelReader<T>> sources,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        ChannelReader<T>[] readers = GoChannelHelpers.CollectSources(sources);
        Channel<T> output = Channel.CreateUnbounded<T>();

        _ = Task.Run(async () =>
        {
            try
            {
                Result<Unit> result = await FanInAsync(readers, output.Writer, completeDestination: false, timeout: timeout, provider: provider, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (result.IsSuccess)
                {
                    output.Writer.TryComplete();
                }
                else
                {
                    output.Writer.TryComplete(GoChannelHelpers.CreateChannelOperationException(result.Error ?? Error.Unspecified()));
                }
            }
            catch (Exception ex)
            {
                output.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        return output.Reader;
    }
}
