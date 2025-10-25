using System.Threading.Channels;

namespace Hugo;

/// <content>
/// Provides fan-out helpers for broadcasting channel data.
/// </content>
public static partial class Go
{
    public static Task<Result<Unit>> FanOutAsync<T>(
        ChannelReader<T> source,
        IReadOnlyList<ChannelWriter<T>> destinations,
        bool completeDestinations = true,
        TimeSpan? deadline = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destinations);

        if (destinations.Count == 0)
        {
            return Task.FromResult(Result.Ok(Unit.Value));
        }

        for (int i = 0; i < destinations.Count; i++)
        {
            if (destinations[i] is null)
            {
                throw new ArgumentException("Destination collection cannot contain null entries.", nameof(destinations));
            }
        }

        if (deadline.HasValue && deadline.Value < TimeSpan.Zero && deadline.Value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

        TimeSpan effectiveDeadline = deadline ?? Timeout.InfiniteTimeSpan;
        TimeProvider effectiveProvider = provider ?? TimeProvider.System;

        return GoChannelHelpers.FanOutAsyncCore(source, destinations, completeDestinations, effectiveDeadline, effectiveProvider, cancellationToken);
    }

    public static IReadOnlyList<ChannelReader<T>> FanOut<T>(
        ChannelReader<T> source,
        int branchCount,
        bool completeBranches = true,
        TimeSpan? deadline = null,
        TimeProvider? provider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (branchCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(branchCount));
        }

        Channel<T>[] channels = new Channel<T>[branchCount];
        ChannelWriter<T>[] writers = new ChannelWriter<T>[branchCount];
        for (int i = 0; i < branchCount; i++)
        {
            Channel<T> channel = Channel.CreateUnbounded<T>();
            channels[i] = channel;
            writers[i] = channel.Writer;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await FanOutAsync(source, writers, completeBranches, deadline, provider, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (completeBranches)
            {
                GoChannelHelpers.CompleteWriters(writers, ex);
            }
            catch
            {
                // Caller manages branch completion when completeBranches is false.
            }
        }, CancellationToken.None);

        ChannelReader<T>[] readers = new ChannelReader<T>[branchCount];
        for (int i = 0; i < branchCount; i++)
        {
            readers[i] = channels[i].Reader;
        }

        return readers;
    }
}
