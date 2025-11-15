using System.Threading.Channels;

namespace Hugo;

internal static class GoChannelHelpers
{
    public static ChannelReader<T>[] CollectSources<T>(IEnumerable<ChannelReader<T>> sources)
    {
        if (sources is ChannelReader<T>[] array)
        {
            return array.Length == 0 ? [] : array;
        }

        if (sources is List<ChannelReader<T>> list)
        {
            return list.Count == 0 ? [] : [.. list];
        }

        List<ChannelReader<T>> collected = [];
        foreach (ChannelReader<T> reader in sources)
        {
            collected.Add(reader);
        }

        return collected.Count == 0 ? [] : [.. collected];
    }

    public static async Task<Result<Go.Unit>> SelectFanInAsyncCore<T>(ChannelReader<T>[] readers, Func<T, CancellationToken, Task<Result<Go.Unit>>> onValue, TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        List<ChannelReader<T>> active = [.. readers];
        if (active.Count == 0)
        {
            return Result.Ok(Go.Unit.Value);
        }

        bool hasDeadline = timeout != Timeout.InfiniteTimeSpan;
        TimeProvider? selectProvider = provider;
        long startTimestamp = 0;
        if (hasDeadline)
        {
            selectProvider ??= TimeProvider.System;
            startTimestamp = selectProvider.GetTimestamp();
        }

        while (active.Count > 0)
        {
            ChannelCase[] cases = new ChannelCase[active.Count];
            for (int i = 0; i < active.Count; i++)
            {
                ChannelReader<T> reader = active[i];
                cases[i] = ChannelCase.Create(reader, onValue);
            }

            Result<Go.Unit> iteration;
            if (hasDeadline)
            {
                TimeSpan elapsed = selectProvider!.GetElapsedTime(startTimestamp);
                TimeSpan remaining = timeout - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return Result.Fail<Go.Unit>(Error.Timeout(timeout));
                }

                iteration = await Go.SelectAsync(remaining, selectProvider, cancellationToken, cases).ConfigureAwait(false);
            }
            else
            {
                iteration = await Go.SelectAsync(selectProvider, cancellationToken, cases).ConfigureAwait(false);
            }

            if (iteration.IsSuccess)
            {
                RemoveCompletedReaders(active);
                continue;
            }

            if (IsSelectDrained(iteration.Error))
            {
                Error? completionError = await WaitForCompletionsAsync(active).ConfigureAwait(false);
                if (completionError is not null)
                {
                    return Result.Fail<Go.Unit>(completionError);
                }

                RemoveCompletedReaders(active);
                if (active.Count == 0)
                {
                    return Result.Ok(Go.Unit.Value);
                }

                continue;
            }

            return iteration;
        }

        return Result.Ok(Go.Unit.Value);

        static void RemoveCompletedReaders(List<ChannelReader<T>> list)
        {
            for (int index = list.Count - 1; index >= 0; index--)
            {
                if (list[index].Completion.IsCompleted)
                {
                    list.RemoveAt(index);
                }
            }
        }

        static async Task<Error?> WaitForCompletionsAsync(List<ChannelReader<T>> list)
        {
            if (list.Count == 0)
            {
                return null;
            }

            Task[] tasks = new Task[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                tasks[i] = list[i].Completion;
            }

            try
            {
                await Task.WhenAll(tasks).ConfigureAwait(false);
                return null;
            }
            catch (OperationCanceledException oce)
            {
                return Error.Canceled(token: oce.CancellationToken);
            }
            catch (Exception ex)
            {
                return Error.FromException(ex);
            }
        }
    }

    public static async Task<Result<Go.Unit>> FanInAsyncCore<T>(ChannelReader<T>[] readers, ChannelWriter<T> destination, bool completeDestination, TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
    {
        try
        {
            Result<Go.Unit> result = await SelectFanInAsyncCore(readers, async (value, ct) =>
            {
                await destination.WriteAsync(value, ct).ConfigureAwait(false);
                return Result.Ok(Go.Unit.Value);
            }, timeout, provider, cancellationToken).ConfigureAwait(false);

            if (!completeDestination)
            {
                return result;
            }

            if (result.IsSuccess)
            {
                destination.TryComplete();
            }
            else
            {
                destination.TryComplete(CreateChannelOperationException(result.Error ?? Error.Unspecified()));
            }

            return result;
        }
        catch (Exception ex)
        {
            if (completeDestination)
            {
                destination.TryComplete(ex);
            }

            throw;
        }
    }

    public static async Task<Result<Go.Unit>> FanOutAsyncCore<T>(ChannelReader<T> source, IReadOnlyList<ChannelWriter<T>> destinations, bool completeDestinations, TimeSpan deadline, TimeProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            while (await source.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (source.TryRead(out T? item))
                {
                    long itemTimestamp = deadline == Timeout.InfiniteTimeSpan ? 0 : provider.GetTimestamp();

                    foreach (ChannelWriter<T> writer in destinations)
                    {
                        Result<Go.Unit> writeResult = await WriteWithDeadlineAsync(writer, item, deadline, provider, itemTimestamp, cancellationToken).ConfigureAwait(false);
                        if (!writeResult.IsFailure)
                        {
                            continue;
                        }

                        if (completeDestinations)
                        {
                            CompleteWriters(destinations, CreateChannelOperationException(writeResult.Error ?? Error.Unspecified()));
                        }

                        return writeResult;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            Error canceled = Error.Canceled(token: cancellationToken.CanBeCanceled ? cancellationToken : null);
            if (completeDestinations)
            {
                CompleteWriters(destinations, CreateChannelOperationException(canceled));
            }

            return Result.Fail<Go.Unit>(canceled);
        }
        catch (Exception ex)
        {
            if (completeDestinations)
            {
                CompleteWriters(destinations, ex);
            }

            return Result.Fail<Go.Unit>(Error.FromException(ex));
        }

        if (source.Completion.IsFaulted)
        {
            Exception fault = source.Completion.Exception?.GetBaseException() ?? new InvalidOperationException("Source channel faulted.");
            if (completeDestinations)
            {
                CompleteWriters(destinations, fault);
            }

            return Result.Fail<Go.Unit>(Error.FromException(fault));
        }

        if (source.Completion.IsCanceled)
        {
            Error canceled = Error.Canceled();
            if (completeDestinations)
            {
                CompleteWriters(destinations, CreateChannelOperationException(canceled));
            }

            return Result.Fail<Go.Unit>(canceled);
        }

        if (completeDestinations)
        {
            CompleteWriters(destinations, exception: null);
        }

        return Result.Ok(Go.Unit.Value);
    }

    public static void CompleteWriters<T>(IReadOnlyList<ChannelWriter<T>> writers, Exception? exception)
    {
        foreach (ChannelWriter<T> writer in writers)
        {
            if (exception is null)
            {
                writer.TryComplete();
            }
            else
            {
                writer.TryComplete(exception);
            }
        }
    }

    public static Exception CreateChannelOperationException(Error error)
    {
        if (error.Code == ErrorCodes.Canceled)
        {
            string message = error.Message;
            if (error.TryGetMetadata("cancellationToken", out CancellationToken token) && token.CanBeCanceled)
            {
                return new OperationCanceledException(message, error.Cause, token);
            }

            return new OperationCanceledException(message, error.Cause);
        }

        return error.Cause ?? new InvalidOperationException(error.ToString());
    }

    private static bool IsSelectDrained(Error? error) => error is { Code: ErrorCodes.SelectDrained };

    private static async Task<Result<Go.Unit>> WriteWithDeadlineAsync<T>(ChannelWriter<T> writer, T value, TimeSpan deadline, TimeProvider provider, long startTimestamp, CancellationToken cancellationToken)
    {
        if (writer.TryWrite(value))
        {
            return Result.Ok(Go.Unit.Value);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (deadline == Timeout.InfiniteTimeSpan)
            {
                bool canWrite = await writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);
                if (!canWrite)
                {
                    return Result.Fail<Go.Unit>(Error.From("Destination channel has completed.", ErrorCodes.ChannelCompleted));
                }
            }
            else
            {
                TimeSpan elapsed = provider.GetElapsedTime(startTimestamp);
                TimeSpan remaining = deadline - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return Result.Fail<Go.Unit>(Error.Timeout(deadline));
                }

                using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<bool> waitTask = writer.WaitToWriteAsync(linkedCts.Token).AsTask();
                Task delayTask = provider.DelayAsync(remaining, linkedCts.Token);

                Task completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
                if (completed == delayTask)
                {
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                    return Result.Fail<Go.Unit>(Error.Timeout(deadline));
                }

                bool canWrite = await waitTask.ConfigureAwait(false);
                if (!canWrite)
                {
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                    return Result.Fail<Go.Unit>(Error.From("Destination channel has completed.", ErrorCodes.ChannelCompleted));
                }

                await linkedCts.CancelAsync().ConfigureAwait(false);
            }

            if (writer.TryWrite(value))
            {
                return Result.Ok(Go.Unit.Value);
            }
        }
    }
}
