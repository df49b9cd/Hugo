using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Hugo;

public static partial class Result
{
    /// <summary>
    /// Writes every result emitted by <paramref name="source"/> to <paramref name="writer"/> and completes the writer when the sequence ends.
    /// Emits a canceled result if the enumeration is canceled.
    /// </summary>
    public static async ValueTask ToChannelAsync<T>(this IAsyncEnumerable<Result<T>> source, ChannelWriter<Result<T>> writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writer);

        await ForwardToChannelInternalAsync(source, writer, cancellationToken, completeWriter: true).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously yields every result available from the reader until it completes or cancellation is requested.
    /// </summary>
    public static async IAsyncEnumerable<Result<T>> ReadAllAsync<T>(this ChannelReader<Result<T>> reader, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);

        while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (reader.TryRead(out var item))
            {
                yield return item;
            }
        }
    }

    /// <summary>
    /// Merges multiple result sequences into a single channel writer and completes the writer when all sources finish.
    /// </summary>
    public static async ValueTask FanInAsync<T>(IEnumerable<IAsyncEnumerable<Result<T>>> sources, ChannelWriter<Result<T>> writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(writer);

        Task[] forwarders = sources
            .Select(source => Go.Run(async _ => await ForwardToChannelInternalAsync(source, writer, cancellationToken, completeWriter: false).ConfigureAwait(false), cancellationToken))
            .ToArray();

        Exception? failure = null;
        try
        {
            await Task.WhenAll(forwarders).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failure = ex;
        }

        if (failure is null)
        {
            writer.TryComplete();
        }
        else
        {
            writer.TryComplete(failure);
            throw failure;
        }
    }

    /// <summary>
    /// Broadcasts every result from the source sequence to each writer, completing all writers when the sequence ends.
    /// </summary>
    public static async ValueTask FanOutAsync<T>(this IAsyncEnumerable<Result<T>> source, IReadOnlyList<ChannelWriter<Result<T>>> writers, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writers);

        try
        {
            await foreach (var result in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var writer in writers)
                {
                    await writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            foreach (var writer in writers)
            {
                writer.TryComplete();
            }
        }
    }

    /// <summary>
    /// Batches successful results into fixed-size windows and yields them as aggregated results.
    /// </summary>
    public static async IAsyncEnumerable<Result<IReadOnlyList<T>>> WindowAsync<T>(this IAsyncEnumerable<Result<T>> source, int size, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);

        var buffer = new List<T>(size);

        await foreach (var result in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (result.IsFailure)
            {
                yield return Fail<IReadOnlyList<T>>(result.Error);
                buffer.Clear();
                continue;
            }

            buffer.Add(result.Value);
            if (buffer.Count >= size)
            {
                yield return Ok<IReadOnlyList<T>>(buffer.ToArray());
                buffer.Clear();
            }
        }

        if (buffer.Count > 0)
        {
            yield return Ok<IReadOnlyList<T>>(buffer.ToArray());
        }
    }

    /// <summary>
    /// Partitions results into <paramref name="trueWriter"/> and <paramref name="falseWriter"/> according to <paramref name="predicate"/>, completing both writers when the sequence ends.
    /// </summary>
    public static ValueTask PartitionAsync<T>(this IAsyncEnumerable<Result<T>> source, Func<T, bool> predicate, ChannelWriter<Result<T>> trueWriter, ChannelWriter<Result<T>> falseWriter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);
        ArgumentNullException.ThrowIfNull(trueWriter);
        ArgumentNullException.ThrowIfNull(falseWriter);

        return new ValueTask(Task.Run(async () =>
        {
            try
            {
                await foreach (var result in source.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var target = result.IsSuccess && predicate(result.Value) ? trueWriter : falseWriter;
                    await target.WriteAsync(result, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                trueWriter.TryComplete();
                falseWriter.TryComplete();
            }
        }, cancellationToken));
    }

    private static async Task ForwardToChannelInternalAsync<T>(IAsyncEnumerable<Result<T>> source, ChannelWriter<Result<T>> writer, CancellationToken cancellationToken, bool completeWriter)
    {
        try
        {
            await foreach (var result in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteAsync(result, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException oce)
        {
            var sentinel = Fail<T>(Error.Canceled(token: oce.CancellationToken));
            if (!writer.TryWrite(sentinel))
            {
                try
                {
                    await writer.WriteAsync(sentinel, CancellationToken.None).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    // Destination closed before the sentinel could be delivered; ignore.
                }
                catch (OperationCanceledException)
                {
                    // Destination write was canceled by an external token; nothing further to do.
                }
            }
        }
        finally
        {
            if (completeWriter)
            {
                writer.TryComplete();
            }
        }
    }
}
