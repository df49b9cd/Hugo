using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Hugo;

using Unit = Go.Unit;

public static partial class Result
{
    /// <summary>Projects an asynchronous sequence into a new asynchronous sequence of results, stopping on first failure.</summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source sequence to project.</param>
    /// <param name="selector">The projection applied to each item.</param>
    /// <param name="cancellationToken">The token used to cancel the projection.</param>
    /// <returns>An asynchronous sequence of results.</returns>
    public static IAsyncEnumerable<Result<TOut>> MapStreamAsync<TIn, TOut>(
        IAsyncEnumerable<TIn> source,
        Func<TIn, CancellationToken, Task<Result<TOut>>> selector,
        CancellationToken cancellationToken = default) => selector is null
            ? throw new ArgumentNullException(nameof(selector))
            : MapStreamAsync(source, (value, token) => new ValueTask<Result<TOut>>(selector(value, token)), cancellationToken);

    /// <summary>Projects an asynchronous sequence into a new asynchronous sequence of results, stopping on first failure.</summary>
    /// <typeparam name="TIn">The type of the source items.</typeparam>
    /// <typeparam name="TOut">The type of the projected results.</typeparam>
    /// <param name="source">The source sequence to project.</param>
    /// <param name="selector">The projection applied to each item.</param>
    /// <param name="cancellationToken">The token used to cancel the projection.</param>
    /// <returns>An asynchronous sequence of results.</returns>
    public static async IAsyncEnumerable<Result<TOut>> MapStreamAsync<TIn, TOut>(
        IAsyncEnumerable<TIn> source,
        Func<TIn, CancellationToken, ValueTask<Result<TOut>>> selector,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        ArgumentNullException.ThrowIfNull(selector);

        var configuredSource = source.WithCancellation(cancellationToken).ConfigureAwait(false);
        var enumerator = configuredSource.GetAsyncEnumerator();
        try
        {
            while (true)
            {
                Result<TOut> failure = default;
                var hasFailure = false;
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException oce)
                {
                    failure = Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
                    hasFailure = true;
                    hasNext = false;
                }
                catch (Exception ex)
                {
                    failure = Fail<TOut>(Error.FromException(ex));
                    hasFailure = true;
                    hasNext = false;
                }

                if (hasFailure)
                {
                    yield return failure;
                    yield break;
                }

                if (!hasNext)
                {
                    yield break;
                }

                Result<TOut> mapped;
                try
                {
                    mapped = await selector(enumerator.Current, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException oce)
                {
                    failure = Fail<TOut>(Error.Canceled(token: oce.CancellationToken));
                    hasFailure = true;
                    mapped = default;
                }
                catch (Exception ex)
                {
                    failure = Fail<TOut>(Error.FromException(ex));
                    hasFailure = true;
                    mapped = default;
                }

                if (hasFailure)
                {
                    yield return failure;
                    yield break;
                }

                yield return mapped;

                if (mapped.IsFailure)
                {
                    yield break;
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync();
        }
    }

    /// <summary>Projects an asynchronous sequence into nested sequences and flattens the results.</summary>
    public static async IAsyncEnumerable<Result<TOut>> FlatMapStreamAsync<TIn, TOut>(
        IAsyncEnumerable<TIn> source,
        Func<TIn, CancellationToken, IAsyncEnumerable<Result<TOut>>> selector,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(selector);

        await foreach (var value in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var inner = selector(value, cancellationToken) ?? throw new InvalidOperationException("Selector returned null stream.");
            await foreach (var projected in inner.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                yield return projected;
                if (projected.IsFailure)
                {
                    yield break;
                }
            }
        }
    }

    /// <summary>Drops successful values that do not satisfy the predicate while leaving failure results untouched.</summary>
    public static async IAsyncEnumerable<Result<T>> FilterStreamAsync<T>(
        IAsyncEnumerable<Result<T>> source,
        Func<T, bool> predicate,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(predicate);

        await foreach (var result in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (result.IsFailure)
            {
                yield return result;
                continue;
            }

            if (predicate(result.Value))
            {
                yield return result;
            }
        }
    }

    /// <summary>
    /// Writes every result emitted by <paramref name="source"/> to <paramref name="writer"/> and completes the writer when the sequence ends.
    /// Emits a canceled result if the enumeration is canceled.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="source">The sequence to forward.</param>
    /// <param name="writer">The channel writer that receives forwarded results.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that completes when the forwarding finishes.</returns>
    public static async ValueTask ToChannelAsync<T>(this IAsyncEnumerable<Result<T>> source, ChannelWriter<Result<T>> writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(writer);

        await ForwardToChannelInternalAsync(source, writer, completeWriter: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Asynchronously yields every result available from the reader until it completes or cancellation is requested.
    /// </summary>
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="reader">The channel reader to enumerate.</param>
    /// <param name="cancellationToken">The token used to cancel the enumeration.</param>
    /// <returns>An asynchronous sequence of results.</returns>
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
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="sources">The result sequences to merge.</param>
    /// <param name="writer">The channel writer that receives merged results.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that completes when the merge finishes.</returns>
    public static async ValueTask FanInAsync<T>(IEnumerable<IAsyncEnumerable<Result<T>>> sources, ChannelWriter<Result<T>> writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(writer);

        Task[] forwarders = [.. sources.Select(source => Go.Run(async _ => await ForwardToChannelInternalAsync(source, writer, completeWriter: false, cancellationToken).ConfigureAwait(false), cancellationToken))];

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
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="source">The sequence to broadcast.</param>
    /// <param name="writers">The writers that receive each result.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that completes when broadcasting finishes.</returns>
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
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="source">The sequence to window.</param>
    /// <param name="size">The number of items per window.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>An asynchronous sequence of windowed results.</returns>
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
    /// <typeparam name="T">The type of the result value.</typeparam>
    /// <param name="source">The sequence to partition.</param>
    /// <param name="predicate">The predicate that determines the target writer.</param>
    /// <param name="trueWriter">The writer that receives results satisfying the predicate.</param>
    /// <param name="falseWriter">The writer that receives results that do not satisfy the predicate.</param>
    /// <param name="cancellationToken">The token used to cancel the operation.</param>
    /// <returns>A task that completes when partitioning finishes.</returns>
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

    private static async Task ForwardToChannelInternalAsync<T>(IAsyncEnumerable<Result<T>> source, ChannelWriter<Result<T>> writer, bool completeWriter, CancellationToken cancellationToken)
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

    /// <summary>
    /// Consumes an asynchronous sequence of results and invokes <paramref name="action"/> for each element.
    /// Stops when the action returns failure and propagates that failure.
    /// </summary>
    public static async ValueTask<Result<Unit>> ForEachAsync<T>(
        this IAsyncEnumerable<Result<T>> source,
        Func<Result<T>, CancellationToken, ValueTask<Result<Unit>>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(action);

        await foreach (var result in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            var callbackResult = await action(result, cancellationToken).ConfigureAwait(false);
            if (callbackResult.IsFailure)
            {
                return callbackResult;
            }
        }

        return Result.Ok(Unit.Value);
    }

    public static async ValueTask<Result<Unit>> ForEachLinkedCancellationAsync<T>(
        this IAsyncEnumerable<Result<T>> source,
        Func<Result<T>, CancellationToken, ValueTask<Result<Unit>>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(action);

        await foreach (var result in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var callbackResult = await action(result, linkedCts.Token).ConfigureAwait(false);
            if (callbackResult.IsFailure)
            {
                return callbackResult;
            }
        }

        return Result.Ok(Unit.Value);
    }

    public static ValueTask<Result<Unit>> TapSuccessEachAsync<T>(
        this IAsyncEnumerable<Result<T>> source,
        Func<T, CancellationToken, ValueTask> tap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tap);
        return source.ForEachAsync(async (result, token) =>
        {
            if (result.IsSuccess)
            {
                await tap(result.Value, token).ConfigureAwait(false);
            }

            return Result.Ok(Unit.Value);
        }, cancellationToken);
    }

    public static ValueTask<Result<Unit>> TapFailureEachAsync<T>(
        this IAsyncEnumerable<Result<T>> source,
        Func<Error, CancellationToken, ValueTask> tap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tap);
        return source.ForEachAsync(async (result, token) =>
        {
            if (result.IsFailure && result.Error is not null)
            {
                await tap(result.Error, token).ConfigureAwait(false);
            }

            return Result.Ok(Unit.Value);
        }, cancellationToken);
    }

    public static async ValueTask<Result<IReadOnlyList<T>>> CollectErrorsAsync<T>(
        this IAsyncEnumerable<Result<T>> source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        var successes = new List<T>();
        var errors = new List<Error>();

        await foreach (var result in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (result.IsSuccess)
            {
                successes.Add(result.Value);
            }
            else
            {
                errors.Add(result.Error ?? Error.Unspecified());
            }
        }

        if (errors.Count == 0)
        {
            return Result.Ok<IReadOnlyList<T>>(successes);
        }

        var aggregate = errors.Count == 1
            ? errors[0]
            : Error.Aggregate("One or more failures occurred while processing the stream.", errors.ToArray());

        return Result.Fail<IReadOnlyList<T>>(aggregate);
    }
}
