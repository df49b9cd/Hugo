using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Hugo;

public static partial class Result
{
    public static async ValueTask ToChannelAsync<T>(this IAsyncEnumerable<Result<T>> source, ChannelWriter<Result<T>> writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writer);

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
            await writer.WriteAsync(Fail<T>(Error.Canceled(token: oce.CancellationToken)), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            writer.TryComplete();
        }
    }

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

    public static ValueTask FanInAsync<T>(IEnumerable<IAsyncEnumerable<Result<T>>> sources, ChannelWriter<Result<T>> writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(writer);

    var tasks = sources.Select(source => Task.Run(async () => await source.ToChannelAsync(writer, cancellationToken), cancellationToken));
    return new ValueTask(Task.WhenAll(tasks));
    }

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

    public static async IAsyncEnumerable<Result<IReadOnlyList<T>>> WindowAsync<T>(this IAsyncEnumerable<Result<T>> source, int size, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size));
        }

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
}
