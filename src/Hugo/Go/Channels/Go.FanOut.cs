using System.Threading.Channels;

namespace Hugo;

/// <content>
/// Provides fan-out helpers for broadcasting channel data.
/// </content>
public static partial class Go
{
    /// <summary>
    /// Broadcasts values from the source reader to multiple destination writers.
    /// </summary>
    /// <typeparam name="T">The payload type emitted by the source.</typeparam>
    /// <param name="source">The channel reader providing values to broadcast.</param>
    /// <param name="destinations">The destination writers that receive each value.</param>
    /// <param name="completeDestinations"><see langword="true"/> to complete each destination when the operation finishes.</param>
    /// <param name="deadline">The optional deadline applied to individual writes.</param>
    /// <param name="provider">The optional time provider used for deadline calculations.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A result indicating whether the operation completed successfully.</returns>
    public static ValueTask<Result<Unit>> FanOutAsync<T>(
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
            return ValueTask.FromResult(Result.Ok(Unit.Value));
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

    /// <summary>
    /// Broadcasts values from the source reader to a set of newly created channels.
    /// </summary>
    /// <typeparam name="T">The payload type emitted by the source.</typeparam>
    /// <param name="source">The channel reader providing values to broadcast.</param>
    /// <param name="branchCount">The number of branches to create.</param>
    /// <param name="completeBranches"><see langword="true"/> to complete each branch when the operation finishes.</param>
    /// <param name="deadline">The optional deadline applied to individual writes.</param>
    /// <param name="provider">The optional time provider used for deadline calculations.</param>
    /// <param name="channelFactory">An optional delegate that creates the branch channels; defaults to an unbounded channel per branch.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A collection of readers that receive the broadcast values.</returns>
    public static IReadOnlyList<ChannelReader<T>> FanOut<T>(
        ChannelReader<T> source,
        int branchCount,
        bool completeBranches = true,
        TimeSpan? deadline = null,
        TimeProvider? provider = null,
        Func<int, Channel<T>>? channelFactory = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(branchCount);

        Channel<T>[] channels = new Channel<T>[branchCount];
        ChannelWriter<T>[] writers = new ChannelWriter<T>[branchCount];
        for (int i = 0; i < branchCount; i++)
        {
            Channel<T> channel = CreateBranchChannel(i, channelFactory);
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

        static Channel<T> CreateBranchChannel(int index, Func<int, Channel<T>>? factory)
        {
            Channel<T>? channel = factory?.Invoke(index);
            return channel ?? Channel.CreateUnbounded<T>();
        }
    }
}
