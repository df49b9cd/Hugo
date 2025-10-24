using System.Diagnostics;
using System.Threading.Channels;

using Hugo.Policies;

using Microsoft.Extensions.Logging;

namespace Hugo;

/// <summary>
/// Provides a collection of static helper methods that emulate core Go language features.
/// For best results, add `using static Hugo.Go;` to your file.
/// </summary>
public static class Go
{
    public static readonly Error CancellationError = Error.Canceled();

    public static Defer Defer(Action action) => new(action);

    /// <summary>
    /// Creates a cancellable delay that honors the supplied <see cref="TimeProvider"/> when provided.
    /// </summary>
    public static Task DelayAsync(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        if (delay < TimeSpan.Zero && delay != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(delay));
        }

        provider ??= TimeProvider.System;
        return provider.DelayAsync(delay, cancellationToken);
    }

    /// <summary>
    /// Creates a ticker that delivers the current time on its channel at the specified period.
    /// </summary>
    public static GoTicker NewTicker(TimeSpan period, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        if (period <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(period), period, "Ticker period must be positive.");
        }

        provider ??= TimeProvider.System;
        TimerChannel timerChannel = TimerChannel.Start(provider, period, period, cancellationToken, singleShot: false);
        return new GoTicker(timerChannel);
    }

    /// <summary>
    /// Returns a channel that delivers periodic ticks at the specified period.
    /// </summary>
    public static ChannelReader<DateTimeOffset> Tick(TimeSpan period, TimeProvider? provider = null, CancellationToken cancellationToken = default) =>
        NewTicker(period, provider, cancellationToken).Reader;

    /// <summary>
    /// Returns a channel that receives the current time once after the provided delay.
    /// </summary>
    public static ChannelReader<DateTimeOffset> After(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        if (delay == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay cannot be infinite.");
        }

        if (delay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(delay), delay, "Delay must be non-negative.");
        }

        provider ??= TimeProvider.System;
        TimerChannel timerChannel = TimerChannel.Start(provider, delay, Timeout.InfiniteTimeSpan, cancellationToken, singleShot: true);
        return timerChannel.Reader;
    }

    /// <summary>
    /// Returns a task that completes once the provided delay has elapsed.
    /// </summary>
    public static Task<DateTimeOffset> AfterAsync(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ChannelReader<DateTimeOffset> reader = After(delay, provider, cancellationToken);
        return reader.ReadAsync(cancellationToken).AsTask();
    }

    /// <summary>
    /// Awaits the first channel case to produce a value.
    /// </summary>
    public static Task<Result<Unit>> SelectAsync(TimeProvider? provider = null, CancellationToken cancellationToken = default, params ChannelCase[] cases) =>
        SelectInternalAsync(cases, defaultCase: null, Timeout.InfiniteTimeSpan, provider, cancellationToken);

    /// <summary>
    /// Awaits the first channel case to produce a value or returns when the timeout elapses.
    /// </summary>
    public static Task<Result<Unit>> SelectAsync(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default, params ChannelCase[] cases) =>
        SelectInternalAsync(cases, defaultCase: null, timeout, provider, cancellationToken);

    /// <summary>
    /// Creates a fluent builder that materializes a typed channel select workflow.
    /// </summary>
    public static SelectBuilder<TResult> Select<TResult>(TimeProvider? provider = null, CancellationToken cancellationToken = default) =>
        new(Timeout.InfiniteTimeSpan, provider, cancellationToken);

    /// <summary>
    /// Creates a fluent builder that materializes a typed channel select workflow with a timeout.
    /// </summary>
    public static SelectBuilder<TResult> Select<TResult>(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default) =>
        new(timeout, provider, cancellationToken);

    /// <summary>
    /// Creates a fluent builder that configures a bounded channel instance.
    /// </summary>
    public static BoundedChannelBuilder<T> BoundedChannel<T>(int capacity) => new(capacity);

    /// <summary>
    /// Creates a fluent builder that configures a prioritized channel instance.
    /// </summary>
    public static PrioritizedChannelBuilder<T> PrioritizedChannel<T>() => new();

    /// <summary>
    /// Creates a fluent builder that configures a prioritized channel instance with the specified priority levels.
    /// </summary>
    public static PrioritizedChannelBuilder<T> PrioritizedChannel<T>(int priorityLevels) => new(priorityLevels);

    internal static async Task<Result<Unit>> SelectInternalAsync(IReadOnlyList<ChannelCase> cases, ChannelCase? defaultCase, TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cases);

        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        provider ??= TimeProvider.System;

        ChannelCase? resolvedDefault = defaultCase;
        List<ChannelCase> caseList = new(cases.Count);
        foreach (ChannelCase channelCase in cases)
        {
            if (channelCase.IsDefault)
            {
                if (resolvedDefault.HasValue)
                {
                    throw new ArgumentException("Only one default case may be supplied.", nameof(cases));
                }

                resolvedDefault = channelCase;
                continue;
            }

            caseList.Add(channelCase);
        }

        if (caseList.Count == 0 && !resolvedDefault.HasValue)
        {
            throw new ArgumentException("At least one channel case must be provided.", nameof(cases));
        }

        using Activity? activity = GoDiagnostics.StartSelectActivity(caseList.Count + (resolvedDefault.HasValue ? 1 : 0), timeout);
        GoDiagnostics.RecordChannelSelectAttempt(activity);
        long startTimestamp = provider.GetTimestamp();

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Attempt immediate reads to honor priority without awaiting.
        List<(int Index, object? State)> immediateCandidates = [];
        for (int i = 0; i < caseList.Count; i++)
        {
            if (caseList[i].TryDequeueImmediately(out object? state))
            {
                immediateCandidates.Add((i, state));
            }
        }

        if (immediateCandidates.Count > 0)
        {
            (int selectedIndex, object? selectedState) = SelectByPriority(immediateCandidates, caseList);
            TimeSpan completionDuration = provider.GetElapsedTime(startTimestamp);

            try
            {
                Result<Unit> continuationResult = await caseList[selectedIndex].ContinueWithAsync(selectedState, linkedCts.Token).ConfigureAwait(false);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, continuationResult.IsSuccess ? null : continuationResult.Error);
                return continuationResult;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Error error = Error.FromException(ex);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, error);
                return Result.Fail<Unit>(error);
            }
            finally
            {
                linkedCts.Cancel();
            }
        }

        if (resolvedDefault.HasValue)
        {
            TimeSpan completionDuration = provider.GetElapsedTime(startTimestamp);
            try
            {
                Result<Unit> defaultResult = await resolvedDefault.Value.ContinueWithAsync(null, linkedCts.Token).ConfigureAwait(false);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, defaultResult.IsSuccess ? null : defaultResult.Error);
                return defaultResult;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Error error = Error.FromException(ex);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, error);
                return Result.Fail<Unit>(error);
            }
            finally
            {
                linkedCts.Cancel();
            }
        }

        List<Task<(bool HasValue, object? Value)>> waitTasks = new(caseList.Count);
        for (int i = 0; i < caseList.Count; i++)
        {
            waitTasks.Add(caseList[i].WaitAsync(linkedCts.Token));
        }

        Task? timeoutTask = timeout == Timeout.InfiniteTimeSpan
            ? null
            : provider.DelayAsync(timeout, linkedCts.Token);

        try
        {
            while (true)
            {
                if (waitTasks.Count == 0)
                {
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                    TimeSpan drainedDuration = provider.GetElapsedTime(startTimestamp);
                    Error drainedError = Error.From("All channel cases completed without yielding a value.", ErrorCodes.SelectDrained);
                    GoDiagnostics.RecordChannelSelectCompleted(drainedDuration, activity, drainedError);
                    return Result.Fail<Unit>(drainedError);
                }

                Task completedTask;
                if (timeoutTask is null)
                {
                    completedTask = await Task.WhenAny(ToTaskArray(waitTasks)).ConfigureAwait(false);
                }
                else
                {
                    Task[] aggregate = new Task[waitTasks.Count + 1];
                    for (int i = 0; i < waitTasks.Count; i++)
                    {
                        aggregate[i] = waitTasks[i];
                    }

                    aggregate[^1] = timeoutTask;
                    completedTask = await Task.WhenAny(aggregate).ConfigureAwait(false);

                    if (completedTask == timeoutTask)
                    {
                        await linkedCts.CancelAsync().ConfigureAwait(false);
                        TimeSpan timeoutDuration = provider.GetElapsedTime(startTimestamp);
                        GoDiagnostics.RecordChannelSelectTimeout(timeoutDuration, activity);
                        return Result.Fail<Unit>(Error.Timeout(timeout));
                    }
                }

                List<int> completedIndices = CollectCompletedIndices(waitTasks);
                if (completedIndices.Count == 0)
                {
                    continue;
                }

                List<int> drainedIndices = [];
                List<(int Index, object? State)> ready = [];

                foreach (int idx in completedIndices)
                {
                    (bool hasValue, object? value) = await waitTasks[idx].ConfigureAwait(false);
                    if (hasValue)
                    {
                        ready.Add((idx, value));
                    }
                    else
                    {
                        drainedIndices.Add(idx);
                    }
                }

                if (ready.Count == 0)
                {
                    if (drainedIndices.Count > 0)
                    {
                        RemoveAtIndices(caseList, waitTasks, drainedIndices);
                    }

                    continue;
                }

                (int selectedIndex, object? selectedState) = SelectByPriority(ready, caseList);
                TimeSpan completionDuration = provider.GetElapsedTime(startTimestamp);

                try
                {
                    Result<Unit> continuationResult = await caseList[selectedIndex].ContinueWithAsync(selectedState, linkedCts.Token).ConfigureAwait(false);
                    GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, continuationResult.IsSuccess ? null : continuationResult.Error);
                    return continuationResult;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Error error = Error.FromException(ex);
                    GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, error);
                    return Result.Fail<Unit>(error);
                }
                finally
                {
                    linkedCts.Cancel();
                }
            }
        }
        catch (OperationCanceledException oce)
        {
            TimeSpan canceledDuration = provider.GetElapsedTime(startTimestamp);
            bool canceledByCaller = cancellationToken is { CanBeCanceled: true, IsCancellationRequested: true };
            GoDiagnostics.RecordChannelSelectCanceled(canceledDuration, activity, canceledByCaller);
            CancellationToken token = cancellationToken.CanBeCanceled ? cancellationToken : oce.CancellationToken;
            return Result.Fail<Unit>(Error.Canceled(token: token.CanBeCanceled ? token : null));
        }

        static Task[] ToTaskArray(List<Task<(bool HasValue, object? Value)>> source)
        {
            Task[] array = new Task[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                array[i] = source[i];
            }

            return array;
        }

        static List<int> CollectCompletedIndices(List<Task<(bool HasValue, object? Value)>> tasks)
        {
            List<int> indices = [];
            for (int i = 0; i < tasks.Count; i++)
            {
                if (tasks[i].IsCompleted)
                {
                    indices.Add(i);
                }
            }

            return indices;
        }

        static (int Index, object? State) SelectByPriority(List<(int Index, object? State)> candidates, List<ChannelCase> cases)
        {
            (int selectedIndex, object? selectedState) = candidates[0];
            int bestPriority = cases[selectedIndex].Priority;

            for (int i = 1; i < candidates.Count; i++)
            {
                (int candidateIndex, object? candidateState) = candidates[i];
                int priority = cases[candidateIndex].Priority;
                if (priority < bestPriority || (priority == bestPriority && candidateIndex < selectedIndex))
                {
                    selectedIndex = candidateIndex;
                    selectedState = candidateState;
                    bestPriority = priority;
                }
            }

            return (selectedIndex, selectedState);
        }

        static void RemoveAtIndices(List<ChannelCase> cases, List<Task<(bool HasValue, object? Value)>> tasks, List<int> indices)
        {
            if (indices.Count == 0)
            {
                return;
            }

            indices.Sort();
            for (int i = indices.Count - 1; i >= 0; i--)
            {
                int index = indices[i];
                cases.RemoveAt(index);
                tasks.RemoveAt(index);
            }
        }
    }


    /// <summary>
    /// Executes multiple operations concurrently using <see cref="Result.WhenAll{T}(IEnumerable{Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}}, ResultExecutionPolicy?, CancellationToken, TimeProvider?)"/> with an optional execution policy.
    /// </summary>
    public static Task<Result<IReadOnlyList<T>>> FanOutAsync<T>(
        IEnumerable<Func<CancellationToken, Task<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(operations);

        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> adapted = operations.Select((operation, index) =>
            operation is null
                ? throw new ArgumentNullException(nameof(operations), $"Operation at index {index} cannot be null.")
                : new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>(
                    (_, ct) => new ValueTask<Result<T>>(operation(ct))));

        return Result.WhenAll(adapted, policy, cancellationToken, timeProvider);
    }

    /// <summary>
    /// Executes multiple operations concurrently and returns the first successful result via <see cref="Result.WhenAny{T}(IEnumerable{Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}}, ResultExecutionPolicy?, CancellationToken, TimeProvider?)"/>.
    /// </summary>
    public static Task<Result<T>> RaceAsync<T>(
        IEnumerable<Func<CancellationToken, Task<Result<T>>>> operations,
        ResultExecutionPolicy? policy = null,
        CancellationToken cancellationToken = default,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(operations);

        IEnumerable<Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>> adapted = operations.Select((operation, index) =>
            operation is null
                ? throw new ArgumentNullException(nameof(operations), $"Operation at index {index} cannot be null.")
                : new Func<ResultPipelineStepContext, CancellationToken, ValueTask<Result<T>>>(
                    (_, ct) => new ValueTask<Result<T>>(operation(ct))));

        return Result.WhenAny(adapted, policy, cancellationToken, timeProvider);
    }

    /// <summary>
    /// Creates a timeout result if the operation does not complete within the specified duration.
    /// </summary>
    public static async Task<Result<T>> WithTimeoutAsync<T>(
        Func<CancellationToken, Task<Result<T>>> operation,
        TimeSpan timeout,
        TimeProvider? timeProvider = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        TimeProvider provider = timeProvider ?? TimeProvider.System;
        static CancellationToken? ResolveCancellationToken(CancellationToken preferred, CancellationToken alternate) =>
            preferred.CanBeCanceled ? preferred : alternate.CanBeCanceled ? alternate : null;

        if (timeout == Timeout.InfiniteTimeSpan)
        {
            try
            {
                return await operation(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException oce)
            {
                return Result.Fail<T>(Error.Canceled(token: ResolveCancellationToken(cancellationToken, oce.CancellationToken)));
            }
        }

        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<Result<T>> operationTask;
        try
        {
            operationTask = operation(linkedCts.Token);
        }
        catch
        {
            await linkedCts.CancelAsync().ConfigureAwait(false);
            throw;
        }

        Task delayTask = provider.DelayAsync(timeout, CancellationToken.None);

        try
        {
            Task completed = await Task.WhenAny(operationTask, delayTask).ConfigureAwait(false);
            if (completed == delayTask)
            {
                await linkedCts.CancelAsync().ConfigureAwait(false);

                try
                {
                    await operationTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                }
                catch
                {
                    // Swallow exceptions from the operation when timeout wins; timeout result takes precedence.
                }

                return Result.Fail<T>(Error.Timeout(timeout));
            }

            return await operationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException oce)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return Result.Fail<T>(Error.Canceled(token: ResolveCancellationToken(cancellationToken, oce.CancellationToken)));
            }

            return Result.Fail<T>(Error.Canceled(token: ResolveCancellationToken(oce.CancellationToken, CancellationToken.None)));
        }
    }

    /// <summary>
    /// Retries an operation with exponential backoff using <see cref="Result.RetryWithPolicyAsync{T}(Func{ResultPipelineStepContext, CancellationToken, ValueTask{Result{T}}}, ResultExecutionPolicy, CancellationToken, TimeProvider?)"/>.
    /// </summary>
    public static async Task<Result<T>> RetryAsync<T>(
        Func<int, CancellationToken, Task<Result<T>>> operation,
        int maxAttempts = 3,
        TimeSpan? initialDelay = null,
        TimeProvider? timeProvider = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxAttempts);

        TimeProvider provider = timeProvider ?? TimeProvider.System;
        ResultExecutionPolicy policy = ResultExecutionBuilders.ExponentialRetryPolicy(
            maxAttempts,
            initialDelay ?? TimeSpan.FromMilliseconds(100));

        int attempt = 0;

        Result<T> finalResult = await Result.RetryWithPolicyAsync(
                async (_, ct) =>
                {
                    int currentAttempt = Interlocked.Increment(ref attempt);

                    try
                    {
                        Result<T> result = await operation(currentAttempt, ct).ConfigureAwait(false);

                        if (result.IsSuccess && currentAttempt > 1)
                        {
                            logger?.LogInformation(
                                "Operation succeeded on attempt {Attempt} of {MaxAttempts}",
                                currentAttempt,
                                maxAttempts);
                        }
                        else if (result.IsFailure && currentAttempt < maxAttempts)
                        {
                            logger?.LogWarning(
                                "Operation failed on attempt {Attempt} of {MaxAttempts}: {Error}",
                                currentAttempt,
                                maxAttempts,
                                result.Error?.Message);
                        }

                        return result;
                    }
                    catch (OperationCanceledException oce)
                    {
                        CancellationToken token = oce.CancellationToken;
                        if (!token.CanBeCanceled && ct.CanBeCanceled)
                        {
                            token = ct;
                        }

                        return Result.Fail<T>(Error.Canceled(token: token.CanBeCanceled ? token : null));
                    }
                    catch (Exception ex)
                    {
                        logger?.LogWarning(
                            ex,
                            "Operation threw on attempt {Attempt} of {MaxAttempts}",
                            currentAttempt,
                            maxAttempts);
                        return Result.Fail<T>(Error.FromException(ex));
                    }
                },
                policy,
                cancellationToken,
                provider)
            .ConfigureAwait(false);

        if (finalResult.IsFailure)
        {
            logger?.LogError(
                "Operation failed after {MaxAttempts} attempts: {Error}",
                maxAttempts,
                finalResult.Error?.Message);
        }

        return finalResult;
    }

    /// <summary>
    /// Drains the provided channel readers until each one completes, invoking <paramref name="onValue"/> for every observed value.
    /// </summary>
    public static Task<Result<Unit>> SelectFanInAsync<T>(IEnumerable<ChannelReader<T>> readers, Func<T, CancellationToken, Task<Result<Unit>>> onValue, TimeSpan? timeout = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readers);

        ArgumentNullException.ThrowIfNull(onValue);

        ChannelReader<T>[] collected = CollectSources(readers);
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
        return SelectFanInAsyncCore(collected, onValue, effectiveTimeout, provider, cancellationToken);
    }

    public static Task<Result<Unit>> SelectFanInAsync<T>(IEnumerable<ChannelReader<T>> readers, Func<T, Task<Result<Unit>>> onValue, TimeSpan? timeout = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return SelectFanInAsync(readers, (value, _) => onValue(value), timeout, provider, cancellationToken);
    }

    public static Task<Result<Unit>> SelectFanInAsync<T>(IEnumerable<ChannelReader<T>> readers, Func<T, CancellationToken, Task> onValue, TimeSpan? timeout = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return SelectFanInAsync(readers, async (value, ct) =>
        {
            await onValue(value, ct).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }, timeout, provider, cancellationToken);
    }

    public static Task<Result<Unit>> SelectFanInAsync<T>(IEnumerable<ChannelReader<T>> readers, Func<T, Task> onValue, TimeSpan? timeout = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(onValue);

        return SelectFanInAsync(readers, async (value, _) =>
        {
            await onValue(value).ConfigureAwait(false);
            return Result.Ok(Unit.Value);
        }, timeout, provider, cancellationToken);
    }

    public static Task<Result<Unit>> SelectFanInAsync<T>(IEnumerable<ChannelReader<T>> readers, Action<T> onValue, TimeSpan? timeout = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
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
    public static Task<Result<Unit>> FanInAsync<T>(IEnumerable<ChannelReader<T>> sources, ChannelWriter<T> destination, bool completeDestination = true, TimeSpan? timeout = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        ArgumentNullException.ThrowIfNull(destination);

        ChannelReader<T>[] readers = CollectSources(sources);
        if (readers.Length != 0)
        {
            for (int i = 0; i < readers.Length; i++)
            {
                if (readers[i] is null)
                {
                    throw new ArgumentException("Source readers cannot contain null entries.", nameof(sources));
                }
            }

            return FanInAsyncCore(readers, destination, completeDestination, timeout ?? Timeout.InfiniteTimeSpan,
                provider, cancellationToken);
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
    public static ChannelReader<T> FanIn<T>(IEnumerable<ChannelReader<T>> sources, TimeSpan? timeout = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        ChannelReader<T>[] readers = CollectSources(sources);
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
                    output.Writer.TryComplete(CreateChannelOperationException(result.Error ?? Error.Unspecified()));
                }
            }
            catch (Exception ex)
            {
                output.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        return output.Reader;
    }

    /// <summary>
    /// Broadcasts each value from a source channel to the provided destination writers.
    /// </summary>
    public static Task<Result<Unit>> FanOutAsync<T>(ChannelReader<T> source, IReadOnlyList<ChannelWriter<T>> destinations, bool completeDestinations = true, TimeSpan? deadline = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        ArgumentNullException.ThrowIfNull(destinations);

        if (deadline.HasValue && deadline.Value < TimeSpan.Zero && deadline.Value != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline));
        }

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

        TimeSpan effectiveDeadline = deadline ?? Timeout.InfiniteTimeSpan;
        provider ??= TimeProvider.System;

        return FanOutAsyncCore(source, destinations, completeDestinations, effectiveDeadline, provider, cancellationToken);
    }

    /// <summary>
    /// Creates <paramref name="branchCount"/> unbounded channels and fans the source reader into each branch.
    /// </summary>
    public static IReadOnlyList<ChannelReader<T>> FanOut<T>(ChannelReader<T> source, int branchCount, bool completeBranches = true, TimeSpan? deadline = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(branchCount);

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
                CompleteWriters(writers, ex);
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

    public readonly record struct Unit
    {
        public static readonly Unit Value = new();
    }

    private static ChannelReader<T>[] CollectSources<T>(IEnumerable<ChannelReader<T>> sources)
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

    private static async Task<Result<Unit>> SelectFanInAsyncCore<T>(ChannelReader<T>[] readers, Func<T, CancellationToken, Task<Result<Unit>>> onValue, TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
    {
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        List<ChannelReader<T>> active = new(readers);
        if (active.Count == 0)
        {
            return Result.Ok(Unit.Value);
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

            Result<Unit> iteration;
            if (hasDeadline)
            {
                TimeSpan elapsed = selectProvider!.GetElapsedTime(startTimestamp);
                TimeSpan remaining = timeout - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return Result.Fail<Unit>(Error.Timeout(timeout));
                }

                iteration = await SelectAsync(remaining, selectProvider, cancellationToken, cases).ConfigureAwait(false);
            }
            else
            {
                iteration = await SelectAsync(selectProvider, cancellationToken, cases).ConfigureAwait(false);
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
                    return Result.Fail<Unit>(completionError);
                }

                RemoveCompletedReaders(active);
                if (active.Count == 0)
                {
                    return Result.Ok(Unit.Value);
                }

                continue;
            }

            return iteration;
        }

        return Result.Ok(Unit.Value);

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

    private static async Task<Result<Unit>> FanInAsyncCore<T>(ChannelReader<T>[] readers, ChannelWriter<T> destination, bool completeDestination, TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
    {
        try
        {
            Result<Unit> result = await SelectFanInAsyncCore(readers, async (value, ct) =>
            {
                await destination.WriteAsync(value, ct).ConfigureAwait(false);
                return Result.Ok(Unit.Value);
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

    private static async Task<Result<Unit>> FanOutAsyncCore<T>(ChannelReader<T> source, IReadOnlyList<ChannelWriter<T>> destinations, bool completeDestinations, TimeSpan deadline, TimeProvider provider, CancellationToken cancellationToken)
    {
        try
        {
            while (await source.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (source.TryRead(out T? item))
                {
                    long itemTimestamp = deadline == Timeout.InfiniteTimeSpan ? 0 : provider.GetTimestamp();

                    foreach (ChannelWriter<T> t in destinations)
                    {
                        Result<Unit> writeResult = await WriteWithDeadlineAsync(t, item, deadline, provider, itemTimestamp, cancellationToken).ConfigureAwait(false);
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

            return Result.Fail<Unit>(canceled);
        }
        catch (Exception ex)
        {
            if (completeDestinations)
            {
                CompleteWriters(destinations, ex);
            }

            return Result.Fail<Unit>(Error.FromException(ex));
        }

        if (source.Completion.IsFaulted)
        {
            Exception fault = source.Completion.Exception?.GetBaseException() ?? new InvalidOperationException("Source channel faulted.");
            if (completeDestinations)
            {
                CompleteWriters(destinations, fault);
            }

            return Result.Fail<Unit>(Error.FromException(fault));
        }

        if (source.Completion.IsCanceled)
        {
            Error canceled = Error.Canceled();
            if (completeDestinations)
            {
                CompleteWriters(destinations, CreateChannelOperationException(canceled));
            }

            return Result.Fail<Unit>(canceled);
        }

        if (completeDestinations)
        {
            CompleteWriters(destinations, null);
        }

        return Result.Ok(Unit.Value);
    }

    private static async Task<Result<Unit>> WriteWithDeadlineAsync<T>(ChannelWriter<T> writer, T value, TimeSpan deadline, TimeProvider provider, long startTimestamp, CancellationToken cancellationToken)
    {
        if (writer.TryWrite(value))
        {
            return Result.Ok(Unit.Value);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (deadline == Timeout.InfiniteTimeSpan)
            {
                bool canWrite = await writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);
                if (!canWrite)
                {
                    return Result.Fail<Unit>(Error.From("Destination channel has completed.", ErrorCodes.ChannelCompleted));
                }
            }
            else
            {
                TimeSpan elapsed = provider.GetElapsedTime(startTimestamp);
                TimeSpan remaining = deadline - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return Result.Fail<Unit>(Error.Timeout(deadline));
                }

                using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task<bool> waitTask = writer.WaitToWriteAsync(linkedCts.Token).AsTask();
                Task delayTask = provider.DelayAsync(remaining, linkedCts.Token);

                Task completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
                if (completed == delayTask)
                {
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                    return Result.Fail<Unit>(Error.Timeout(deadline));
                }

                bool canWrite = await waitTask.ConfigureAwait(false);
                if (!canWrite)
                {
                    await linkedCts.CancelAsync().ConfigureAwait(false);
                    return Result.Fail<Unit>(Error.From("Destination channel has completed.", ErrorCodes.ChannelCompleted));
                }

                await linkedCts.CancelAsync().ConfigureAwait(false);
            }

            if (writer.TryWrite(value))
            {
                return Result.Ok(Unit.Value);
            }
        }
    }

    private static void CompleteWriters<T>(IReadOnlyList<ChannelWriter<T>> writers, Exception? exception)
    {
        foreach (ChannelWriter<T> t in writers)
        {
            if (exception is null)
            {
                t.TryComplete();
                continue;
            }

            t.TryComplete(exception);
        }
    }

    private static bool IsSelectDrained(Error? error) => error is { Code: ErrorCodes.SelectDrained };

    private static Exception CreateChannelOperationException(Error error)
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

    public readonly struct GoTicker : IAsyncDisposable, IDisposable
    {
        private readonly TimerChannel? _channel;

        internal GoTicker(TimerChannel channel)
        {
            _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        }

        private TimerChannel Channel => _channel ?? throw new InvalidOperationException("Ticker is not initialized.");

        public ChannelReader<DateTimeOffset> Reader => Channel.Reader;

        public ValueTask<DateTimeOffset> ReadAsync(CancellationToken cancellationToken = default) => Channel.ReadAsync(cancellationToken);

        public bool TryRead(out DateTimeOffset value) => Channel.TryRead(out value);

        public void Stop() => Channel.Dispose();

        public ValueTask StopAsync() => Channel.DisposeAsync();

        public void Dispose()
        {
            Stop();
            GC.SuppressFinalize(this);
        }

        public ValueTask DisposeAsync() => StopAsync();
    }

    /// <summary>
    /// Runs a function on a background thread. For tasks that should be tracked by a WaitGroup,
    /// prefer using the `wg.Go(...)` extension method for a cleaner syntax.
    /// </summary>
    public static Task Run(Func<Task> func) => func is null ? throw new ArgumentNullException(nameof(func)) : Task.Run(func);

    /// <summary>
    /// Runs a cancelable function on a background thread.
    /// </summary>
    public static Task Run(Func<CancellationToken, Task> func, CancellationToken cancellationToken = default) => func is null ? throw new ArgumentNullException(nameof(func)) : Task.Run(() => func(cancellationToken), cancellationToken);

    public static Channel<T> MakeChannel<T>(
        int? capacity = null,
        BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait,
        bool singleReader = false,
        bool singleWriter = false
    )
    {
        if (capacity is > 0)
        {
            BoundedChannelOptions options = new(capacity.Value)
            {
                FullMode = fullMode,
                SingleReader = singleReader,
                SingleWriter = singleWriter
            };

            return Channel.CreateBounded<T>(options);
        }

        UnboundedChannelOptions unboundedOptions = new()
        {
            SingleReader = singleReader,
            SingleWriter = singleWriter
        };

        return Channel.CreateUnbounded<T>(unboundedOptions);
    }

    public static Channel<T> MakeChannel<T>(BoundedChannelOptions options) => options is null ? throw new ArgumentNullException(nameof(options)) : Channel.CreateBounded<T>(options);

    public static Channel<T> MakeChannel<T>(UnboundedChannelOptions options) => options is null ? throw new ArgumentNullException(nameof(options)) : Channel.CreateUnbounded<T>(options);

    public static PrioritizedChannel<T> MakeChannel<T>(PrioritizedChannelOptions options) => options is null ? throw new ArgumentNullException(nameof(options)) : new PrioritizedChannel<T>(options);

    public static PrioritizedChannel<T> MakePrioritizedChannel<T>(
        int priorityLevels,
        int? capacityPerLevel = null,
        BoundedChannelFullMode fullMode = BoundedChannelFullMode.Wait,
        bool singleReader = false,
        bool singleWriter = false,
        int? defaultPriority = null)
    {
        PrioritizedChannelOptions options = new()
        {
            PriorityLevels = priorityLevels,
            CapacityPerLevel = capacityPerLevel,
            FullMode = fullMode,
            SingleReader = singleReader,
            SingleWriter = singleWriter
        };

        if (defaultPriority.HasValue)
        {
            options.DefaultPriority = defaultPriority.Value;
        }

        return MakeChannel<T>(options);
    }

    public static Result<T> Ok<T>(T value) => Result.Ok(value);

    public static Result<T> Err<T>(Error? error) => Result.Fail<T>(error ?? Error.Unspecified());

    public static Result<T> Err<T>(string message, string? code = null) =>
        Result.Fail<T>(Error.From(message, code));

    public static Result<T> Err<T>(Exception exception, string? code = null) =>
        Result.Fail<T>(Error.FromException(exception, code));
}

/// <summary>
/// Provides extension methods for the Go# framework.
/// </summary>
public static class GoWaitGroupExtensions
{
    /// <summary>
    /// Runs a function on a background thread and adds it to the WaitGroup, similar to `go func()`.
    /// This is the recommended way to run concurrent tasks that need to be awaited.
    /// </summary>
    /// <param name="wg">The WaitGroup to add the task to.</param>
    /// <param name="func">The async function to execute.</param>
    /// <example>wg.Go(async () => { ... });</example>
    public static void Go(this WaitGroup wg, Func<Task> func)
    {
        ArgumentNullException.ThrowIfNull(wg);

        ArgumentNullException.ThrowIfNull(func);

        wg.Add(Task.Run(func));
    }

    /// <summary>
    /// Runs a cancellation-aware function on a background thread and adds it to the WaitGroup.
    /// </summary>
    public static void Go(this WaitGroup wg, Func<CancellationToken, Task> func, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wg);

        ArgumentNullException.ThrowIfNull(func);

        wg.Go(() => func(cancellationToken), cancellationToken);
    }
}
