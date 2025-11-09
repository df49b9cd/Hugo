using System.Threading.Channels;
using System.Threading.Tasks;

namespace Hugo;

/// <content>
/// Provides fan-in helpers for aggregating channel data.
/// </content>
public static partial class Go
{
    /// <summary>
    /// Drains the provided channel readers until each one completes, invoking <paramref name="onValue"/> for every observed value using a value task continuation.
    /// </summary>
    /// <typeparam name="T">The payload type emitted by the readers.</typeparam>
    /// <param name="readers">The channel readers to drain.</param>
    /// <param name="onValue">The continuation invoked for each value.</param>
    /// <param name="timeout">The optional timeout applied to the fan-in operation.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result indicating whether the operation completed successfully.</returns>
    public static Task<Result<Unit>> SelectFanInValueTaskAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, CancellationToken, ValueTask<Result<Unit>>> onValue,
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
        return GoChannelHelpers.SelectFanInAsyncCore(collected, (value, ct) => InvokeValueTask(onValue, value, ct), effectiveTimeout, provider, cancellationToken);
    }

    /// <summary>
    /// Drains the provided channel readers until each one completes, invoking <paramref name="onValue"/> for every observed value.
    /// </summary>
    /// <typeparam name="T">The payload type emitted by the readers.</typeparam>
    /// <param name="readers">The channel readers to drain.</param>
    /// <param name="onValue">The continuation invoked for each value.</param>
    /// <param name="timeout">The optional timeout applied to the fan-in operation.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result indicating whether the operation completed successfully.</returns>
    public static Task<Result<Unit>> SelectFanInAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, CancellationToken, Task<Result<Unit>>> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        Func<T, CancellationToken, ValueTask<Result<Unit>>> handler = (value, ct) => new ValueTask<Result<Unit>>(onValue(value, ct));
        return SelectFanInValueTaskAsync(readers, handler, timeout, provider, cancellationToken);
    }

    /// <summary>Drains the provided readers using a continuation without a cancellation token.</summary>
    /// <typeparam name="T">The payload type emitted by the readers.</typeparam>
    /// <param name="readers">The channel readers to drain.</param>
    /// <param name="onValue">The continuation invoked for each value.</param>
    /// <param name="timeout">The optional timeout applied to the fan-in operation.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result indicating whether the operation completed successfully.</returns>
    public static Task<Result<Unit>> SelectFanInAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, Task<Result<Unit>>> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        Func<T, CancellationToken, ValueTask<Result<Unit>>> handler = (value, _) => new ValueTask<Result<Unit>>(onValue(value));
        return SelectFanInValueTaskAsync(readers, handler, timeout, provider, cancellationToken);
    }

    /// <summary>Drains the provided readers using a value task-returning continuation without a cancellation token.</summary>
    /// <typeparam name="T">The payload type emitted by the readers.</typeparam>
    /// <param name="readers">The channel readers to drain.</param>
    /// <param name="onValue">The continuation invoked for each value.</param>
    /// <param name="timeout">The optional timeout applied to the fan-in operation.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result indicating whether the operation completed successfully.</returns>
    public static Task<Result<Unit>> SelectFanInValueTaskAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, ValueTask<Result<Unit>>> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        Func<T, CancellationToken, ValueTask<Result<Unit>>> handler = (value, _) => onValue(value);
        return SelectFanInValueTaskAsync(readers, handler, timeout, provider, cancellationToken);
    }

    /// <summary>Drains the provided readers invoking a task-returning continuation.</summary>
    /// <typeparam name="T">The payload type emitted by the readers.</typeparam>
    /// <param name="readers">The channel readers to drain.</param>
    /// <param name="onValue">The continuation invoked for each value.</param>
    /// <param name="timeout">The optional timeout applied to the fan-in operation.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result indicating whether the operation completed successfully.</returns>
    public static Task<Result<Unit>> SelectFanInAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, CancellationToken, Task> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        Func<T, CancellationToken, ValueTask<Result<Unit>>> handler = async (value, ct) =>
        {
            await onValue(value, ct).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        };

        return SelectFanInValueTaskAsync(readers, handler, timeout, provider, cancellationToken);
    }

    /// <summary>Drains the provided readers invoking a value task-returning continuation.</summary>
    /// <typeparam name="T">The payload type emitted by the readers.</typeparam>
    /// <param name="readers">The channel readers to drain.</param>
    /// <param name="onValue">The continuation invoked for each value.</param>
    /// <param name="timeout">The optional timeout applied to the fan-in operation.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result indicating whether the operation completed successfully.</returns>
    public static Task<Result<Unit>> SelectFanInValueTaskAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, CancellationToken, ValueTask> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        Func<T, CancellationToken, ValueTask<Result<Unit>>> handler = async (value, ct) =>
        {
            await onValue(value, ct).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        };

        return SelectFanInValueTaskAsync(readers, handler, timeout, provider, cancellationToken);
    }

    /// <summary>Drains the provided readers invoking a task-returning continuation without a cancellation token.</summary>
    /// <typeparam name="T">The payload type emitted by the readers.</typeparam>
    /// <param name="readers">The channel readers to drain.</param>
    /// <param name="onValue">The continuation invoked for each value.</param>
    /// <param name="timeout">The optional timeout applied to the fan-in operation.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result indicating whether the operation completed successfully.</returns>
    public static Task<Result<Unit>> SelectFanInAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, Task> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        Func<T, CancellationToken, ValueTask<Result<Unit>>> handler = async (value, _) =>
        {
            await onValue(value).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        };

        return SelectFanInValueTaskAsync(readers, handler, timeout, provider, cancellationToken);
    }

    /// <summary>Drains the provided readers invoking a value task-returning continuation without a cancellation token.</summary>
    /// <typeparam name="T">The payload type emitted by the readers.</typeparam>
    /// <param name="readers">The channel readers to drain.</param>
    /// <param name="onValue">The continuation invoked for each value.</param>
    /// <param name="timeout">The optional timeout applied to the fan-in operation.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result indicating whether the operation completed successfully.</returns>
    public static Task<Result<Unit>> SelectFanInValueTaskAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Func<T, ValueTask> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        Func<T, CancellationToken, ValueTask<Result<Unit>>> handler = async (value, _) =>
        {
            await onValue(value).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        };

        return SelectFanInValueTaskAsync(readers, handler, timeout, provider, cancellationToken);
    }

    /// <summary>Drains the provided readers invoking a synchronous callback.</summary>
    /// <typeparam name="T">The payload type emitted by the readers.</typeparam>
    /// <param name="readers">The channel readers to drain.</param>
    /// <param name="onValue">The callback invoked for each value.</param>
    /// <param name="timeout">The optional timeout applied to the fan-in operation.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result indicating whether the operation completed successfully.</returns>
    public static Task<Result<Unit>> SelectFanInAsync<T>(
        IEnumerable<ChannelReader<T>> readers,
        Action<T> onValue,
        TimeSpan? timeout = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        Func<T, CancellationToken, ValueTask<Result<Unit>>> handler = (value, _) =>
        {
            onValue(value);
            return new ValueTask<Result<Unit>>(Result.Ok(Unit.Value));
        };

        return SelectFanInValueTaskAsync(readers, handler, timeout, provider, cancellationToken);
    }

    /// <summary>
    /// Fans multiple source channels into a destination writer, optionally completing the writer when the sources finish.
    /// </summary>
    /// <typeparam name="T">The payload type emitted by the sources.</typeparam>
    /// <param name="sources">The channel readers to fan in.</param>
    /// <param name="destination">The destination writer that receives aggregated values.</param>
    /// <param name="completeDestination"><see langword="true"/> to complete the destination once the sources finish.</param>
    /// <param name="timeout">The optional timeout applied to the fan-in operation.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result indicating whether the operation completed successfully.</returns>
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
    /// <typeparam name="T">The payload type emitted by the sources.</typeparam>
    /// <param name="sources">The channel readers to fan in.</param>
    /// <param name="timeout">The optional timeout applied to the fan-in operation.</param>
    /// <param name="provider">The optional time provider used for timeout calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A channel reader that produces values from the aggregated sources.</returns>
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

    private static Task<Result<Unit>> InvokeValueTask<T>(Func<T, CancellationToken, ValueTask<Result<Unit>>> handler, T value, CancellationToken cancellationToken)
    {
        ValueTask<Result<Unit>> valueTask;
        try
        {
            valueTask = handler(value, cancellationToken);
        }
        catch (Exception ex)
        {
            return Task.FromException<Result<Unit>>(ex);
        }

        if (valueTask.IsCompletedSuccessfully)
        {
            return Task.FromResult(valueTask.Result);
        }

        return valueTask.AsTask();
    }
}
