using System.Threading.Channels;

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
        var timerChannel = TimerChannel.Start(provider, period, period, cancellationToken, singleShot: false);
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
        var timerChannel = TimerChannel.Start(provider, delay, Timeout.InfiniteTimeSpan, cancellationToken, singleShot: true);
        return timerChannel.Reader;
    }

    /// <summary>
    /// Returns a task that completes once the provided delay has elapsed.
    /// </summary>
    public static Task<DateTimeOffset> AfterAsync(TimeSpan delay, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        var reader = After(delay, provider, cancellationToken);
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

    private static async Task<Result<Unit>> SelectInternalAsync(IReadOnlyList<ChannelCase> cases, ChannelCase? defaultCase, TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cases);

        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

        provider ??= TimeProvider.System;

        var resolvedDefault = defaultCase;
        var caseList = new List<ChannelCase>(cases.Count);
        for (var i = 0; i < cases.Count; i++)
        {
            var channelCase = cases[i];
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

        using var activity = GoDiagnostics.StartSelectActivity(caseList.Count + (resolvedDefault.HasValue ? 1 : 0), timeout);
        GoDiagnostics.RecordChannelSelectAttempt(activity);
        var startTimestamp = provider.GetTimestamp();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Attempt immediate reads to honor priority without awaiting.
        var immediateCandidates = new List<(int Index, object? State)>();
        for (var i = 0; i < caseList.Count; i++)
        {
            if (caseList[i].TryDequeueImmediately(out var state))
            {
                immediateCandidates.Add((i, state));
            }
        }

        if (immediateCandidates.Count > 0)
        {
            var (selectedIndex, selectedState) = SelectByPriority(immediateCandidates, caseList);
            linkedCts.Cancel();
            var completionDuration = provider.GetElapsedTime(startTimestamp);

            try
            {
                var continuationResult = await caseList[selectedIndex].ContinueWithAsync(selectedState, cancellationToken).ConfigureAwait(false);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, continuationResult.IsSuccess ? null : continuationResult.Error);
                return continuationResult;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = Error.FromException(ex);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, error);
                return Result.Fail<Unit>(error);
            }
        }

        if (resolvedDefault.HasValue)
        {
            linkedCts.Cancel();
            var completionDuration = provider.GetElapsedTime(startTimestamp);
            try
            {
                var defaultResult = await resolvedDefault.Value.ContinueWithAsync(null, cancellationToken).ConfigureAwait(false);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, defaultResult.IsSuccess ? null : defaultResult.Error);
                return defaultResult;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                var error = Error.FromException(ex);
                GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, error);
                return Result.Fail<Unit>(error);
            }
        }

        var waitTasks = new List<Task<(bool HasValue, object? Value)>>(caseList.Count);
        for (var i = 0; i < caseList.Count; i++)
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
                    linkedCts.Cancel();
                    var drainedDuration = provider.GetElapsedTime(startTimestamp);
                    var drainedError = Error.From("All channel cases completed without yielding a value.", ErrorCodes.SelectDrained);
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
                    var aggregate = new Task[waitTasks.Count + 1];
                    for (var i = 0; i < waitTasks.Count; i++)
                    {
                        aggregate[i] = waitTasks[i];
                    }

                    aggregate[^1] = timeoutTask;
                    completedTask = await Task.WhenAny(aggregate).ConfigureAwait(false);

                    if (completedTask == timeoutTask)
                    {
                        linkedCts.Cancel();
                        var timeoutDuration = provider.GetElapsedTime(startTimestamp);
                        GoDiagnostics.RecordChannelSelectTimeout(timeoutDuration, activity);
                        return Result.Fail<Unit>(Error.Timeout(timeout));
                    }
                }

                var completedIndices = CollectCompletedIndices(waitTasks);
                if (completedIndices.Count == 0)
                {
                    continue;
                }

                var drainedIndices = new List<int>();
                var ready = new List<(int Index, object? State)>();

                foreach (var idx in completedIndices)
                {
                    var (hasValue, value) = await waitTasks[idx].ConfigureAwait(false);
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

                var (selectedIndex, selectedState) = SelectByPriority(ready, caseList);
                linkedCts.Cancel();
                var completionDuration = provider.GetElapsedTime(startTimestamp);

                try
                {
                    var continuationResult = await caseList[selectedIndex].ContinueWithAsync(selectedState, cancellationToken).ConfigureAwait(false);
                    GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, continuationResult.IsSuccess ? null : continuationResult.Error);
                    return continuationResult;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var error = Error.FromException(ex);
                    GoDiagnostics.RecordChannelSelectCompleted(completionDuration, activity, error);
                    return Result.Fail<Unit>(error);
                }
            }
        }
        catch (OperationCanceledException oce)
        {
            var canceledDuration = provider.GetElapsedTime(startTimestamp);
            var canceledByCaller = cancellationToken.CanBeCanceled && cancellationToken.IsCancellationRequested;
            GoDiagnostics.RecordChannelSelectCanceled(canceledDuration, activity, canceledByCaller);
            var token = cancellationToken.CanBeCanceled ? cancellationToken : oce.CancellationToken;
            return Result.Fail<Unit>(Error.Canceled(token: token.CanBeCanceled ? token : null));
        }

        static Task[] ToTaskArray(List<Task<(bool HasValue, object? Value)>> source)
        {
            var array = new Task[source.Count];
            for (var i = 0; i < source.Count; i++)
            {
                array[i] = source[i];
            }

            return array;
        }

        static List<int> CollectCompletedIndices(List<Task<(bool HasValue, object? Value)>> tasks)
        {
            var indices = new List<int>();
            for (var i = 0; i < tasks.Count; i++)
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
            var (selectedIndex, selectedState) = candidates[0];
            var bestPriority = cases[selectedIndex].Priority;

            for (var i = 1; i < candidates.Count; i++)
            {
                var (candidateIndex, candidateState) = candidates[i];
                var priority = cases[candidateIndex].Priority;
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
            for (var i = indices.Count - 1; i >= 0; i--)
            {
                var index = indices[i];
                cases.RemoveAt(index);
                tasks.RemoveAt(index);
            }
        }
    }


    /// <summary>
    /// Drains the provided channel readers until each one completes, invoking <paramref name="onValue"/> for every observed value.
    /// </summary>
    public static Task<Result<Unit>> SelectFanInAsync<T>(IEnumerable<ChannelReader<T>> readers, Func<T, CancellationToken, Task<Result<Unit>>> onValue, TimeSpan? timeout = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readers);

        ArgumentNullException.ThrowIfNull(onValue);

        var collected = CollectSources(readers);
        if (collected.Length == 0)
        {
            return Task.FromResult(Result.Ok(Unit.Value));
        }

        for (var i = 0; i < collected.Length; i++)
        {
            if (collected[i] is null)
            {
                throw new ArgumentException("Reader collection cannot contain null entries.", nameof(readers));
            }
        }

        var effectiveTimeout = timeout ?? Timeout.InfiniteTimeSpan;
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

        var readers = CollectSources(sources);
        if (readers.Length == 0)
        {
            if (completeDestination)
            {
                destination.TryComplete();
            }

            return Task.FromResult(Result.Ok(Unit.Value));
        }

        for (var i = 0; i < readers.Length; i++)
        {
            if (readers[i] is null)
            {
                throw new ArgumentException("Source readers cannot contain null entries.", nameof(sources));
            }
        }

        return FanInAsyncCore(readers, destination, completeDestination, timeout ?? Timeout.InfiniteTimeSpan, provider, cancellationToken);
    }

    /// <summary>
    /// Fans multiple source channels into a newly created channel.
    /// </summary>
    public static ChannelReader<T> FanIn<T>(IEnumerable<ChannelReader<T>> sources, TimeSpan? timeout = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);

        var readers = CollectSources(sources);
        var output = Channel.CreateUnbounded<T>();

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await FanInAsync(readers, output.Writer, completeDestination: false, timeout: timeout, provider: provider, cancellationToken: cancellationToken).ConfigureAwait(false);
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

        for (var i = 0; i < destinations.Count; i++)
        {
            if (destinations[i] is null)
            {
                throw new ArgumentException("Destination collection cannot contain null entries.", nameof(destinations));
            }
        }

        var effectiveDeadline = deadline ?? Timeout.InfiniteTimeSpan;
        provider ??= TimeProvider.System;

        return FanOutAsyncCore(source, destinations, completeDestinations, effectiveDeadline, provider, cancellationToken);
    }

    /// <summary>
    /// Creates <paramref name="branchCount"/> unbounded channels and fans the source reader into each branch.
    /// </summary>
    public static IReadOnlyList<ChannelReader<T>> FanOut<T>(ChannelReader<T> source, int branchCount, bool completeBranches = true, TimeSpan? deadline = null, TimeProvider? provider = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (branchCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(branchCount));
        }

        var channels = new Channel<T>[branchCount];
        var writers = new ChannelWriter<T>[branchCount];
        for (var i = 0; i < branchCount; i++)
        {
            var channel = Channel.CreateUnbounded<T>();
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

        var readers = new ChannelReader<T>[branchCount];
        for (var i = 0; i < branchCount; i++)
        {
            readers[i] = channels[i].Reader;
        }

        return readers;
    }


    public sealed class SelectBuilder<TResult>
    {
    private sealed record SelectCaseRegistration(int Priority, Func<TaskCompletionSource<Result<TResult>>, ChannelCase> Factory);

    private readonly List<SelectCaseRegistration> _caseFactories = [];
    private Func<TaskCompletionSource<Result<TResult>>, ChannelCase>? _defaultFactory;
        private readonly TimeSpan _timeout;
        private readonly TimeProvider? _provider;
        private readonly CancellationToken _cancellationToken;

        internal SelectBuilder(TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(timeout));
            }

            _timeout = timeout;
            _provider = provider;
            _cancellationToken = cancellationToken;
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
            AddCase(reader, onValue, priority: 0);

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
            AddCase(reader, onValue, priority);

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, Task<Result<TResult>>> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, (value, _) => onValue(value));
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, Task<Result<TResult>>> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, priority, (value, _) => onValue(value));
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<TResult>> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, async (value, ct) => Result.Ok(await onValue(value, ct).ConfigureAwait(false)));
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, CancellationToken, Task<TResult>> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, priority, async (value, ct) => Result.Ok(await onValue(value, ct).ConfigureAwait(false)));
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, Task<TResult>> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, async (value, _) => Result.Ok(await onValue(value).ConfigureAwait(false)));
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, Task<TResult>> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, priority, async (value, _) => Result.Ok(await onValue(value).ConfigureAwait(false)));
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, TResult> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, (value, _) => Task.FromResult(Result.Ok(onValue(value))));
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, int priority, Func<T, TResult> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, priority, (value, _) => Task.FromResult(Result.Ok(onValue(value))));
        }

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
            Case(template.Reader, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
            Case(template.Reader, priority, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, Task<Result<TResult>>> onValue) =>
            Case(template.Reader, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, Task<Result<TResult>>> onValue) =>
            Case(template.Reader, priority, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, CancellationToken, Task<TResult>> onValue) =>
            Case(template.Reader, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, CancellationToken, Task<TResult>> onValue) =>
            Case(template.Reader, priority, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, Task<TResult>> onValue) =>
            Case(template.Reader, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, Task<TResult>> onValue) =>
            Case(template.Reader, priority, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, TResult> onValue) =>
            Case(template.Reader, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, int priority, Func<T, TResult> onValue) =>
            Case(template.Reader, priority, onValue);

        public SelectBuilder<TResult> Default(Func<CancellationToken, Task<Result<TResult>>> onDefault)
        {
            ArgumentNullException.ThrowIfNull(onDefault);

            EnsureDefaultNotConfigured();
            _defaultFactory = completion => CreateDefaultCase(onDefault, completion, priority: 0);
            return this;
        }

        public SelectBuilder<TResult> Default(Func<Task<Result<TResult>>> onDefault)
        {
            ArgumentNullException.ThrowIfNull(onDefault);

            return Default(_ => onDefault());
        }

        public SelectBuilder<TResult> Default(Func<CancellationToken, Task<TResult>> onDefault)
        {
            ArgumentNullException.ThrowIfNull(onDefault);

            return Default(async (ct) => Result.Ok(await onDefault(ct).ConfigureAwait(false)));
        }

        public SelectBuilder<TResult> Default(Func<Task<TResult>> onDefault)
        {
            ArgumentNullException.ThrowIfNull(onDefault);

            return Default(async _ => Result.Ok(await onDefault().ConfigureAwait(false)));
        }

        public SelectBuilder<TResult> Default(Func<TResult> onDefault)
        {
            ArgumentNullException.ThrowIfNull(onDefault);

            return Default(_ => Task.FromResult(Result.Ok(onDefault())));
        }

        public SelectBuilder<TResult> Default(TResult value) =>
            Default(() => value);

        public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<CancellationToken, Task<Result<TResult>>> onDeadline, int priority = 0, TimeProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(onDeadline);

            if (dueIn < TimeSpan.Zero || dueIn == Timeout.InfiniteTimeSpan)
            {
                throw new ArgumentOutOfRangeException(nameof(dueIn));
            }

            var effectiveProvider = provider ?? _provider ?? TimeProvider.System;
            var reader = Go.After(dueIn, effectiveProvider, _cancellationToken);
            return AddCase(reader, async (_, ct) => await onDeadline(ct).ConfigureAwait(false), priority);
        }

        public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<Task<Result<TResult>>> onDeadline, int priority = 0, TimeProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(onDeadline);

            return Deadline(dueIn, _ => onDeadline(), priority, provider);
        }

        public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<CancellationToken, Task<TResult>> onDeadline, int priority = 0, TimeProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(onDeadline);

            return Deadline(dueIn, async ct => Result.Ok(await onDeadline(ct).ConfigureAwait(false)), priority, provider);
        }

        public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<Task<TResult>> onDeadline, int priority = 0, TimeProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(onDeadline);

            return Deadline(dueIn, async _ => Result.Ok(await onDeadline().ConfigureAwait(false)), priority, provider);
        }

        public SelectBuilder<TResult> Deadline(TimeSpan dueIn, Func<TResult> onDeadline, int priority = 0, TimeProvider? provider = null)
        {
            ArgumentNullException.ThrowIfNull(onDeadline);

            return Deadline(dueIn, _ => Task.FromResult(Result.Ok(onDeadline())), priority, provider);
        }

        public SelectBuilder<TResult> Deadline(TimeSpan dueIn, TResult value, int priority = 0, TimeProvider? provider = null) =>
            Deadline(dueIn, () => value, priority, provider);

        private SelectBuilder<TResult> AddCase<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<Result<TResult>>> onValue, int priority)
        {
            ArgumentNullException.ThrowIfNull(reader);
            ArgumentNullException.ThrowIfNull(onValue);

            _caseFactories.Add(new SelectCaseRegistration(priority, completion => CreateCase(reader, onValue, completion, priority)));
            return this;
        }

        private void EnsureDefaultNotConfigured()
        {
            if (_defaultFactory is not null)
            {
                throw new InvalidOperationException("A default case has already been configured for this select builder.");
            }
        }

        public async Task<Result<TResult>> ExecuteAsync()
        {
            if (_caseFactories.Count == 0 && _defaultFactory is null)
            {
                throw new InvalidOperationException("At least one channel case or a default case must be configured before executing the select.");
            }

            var completion = new TaskCompletionSource<Result<TResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cases = new ChannelCase[_caseFactories.Count];
            for (var i = 0; i < _caseFactories.Count; i++)
            {
                cases[i] = _caseFactories[i].Factory(completion);
            }

            ChannelCase? defaultCase = _defaultFactory is null ? null : _defaultFactory(completion);

            var selectResult = await Go.SelectInternalAsync(cases, defaultCase, _timeout, _provider, _cancellationToken).ConfigureAwait(false);

            if (completion.Task.IsCompleted)
            {
                return await completion.Task.ConfigureAwait(false);
            }

            if (selectResult.IsFailure)
            {
                return Result.Fail<TResult>(selectResult.Error ?? Error.Unspecified());
            }

            return await completion.Task.ConfigureAwait(false);
        }

        private ChannelCase CreateCase<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<Result<TResult>>> onValue, TaskCompletionSource<Result<TResult>> completion, int priority)
        {
            var channelCase = ChannelCase.Create(reader, async (value, ct) =>
            {
                Result<TResult> caseResult;
                try
                {
                    caseResult = await onValue(value, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    caseResult = Result.Fail<TResult>(Error.FromException(ex));
                }

                completion.TrySetResult(caseResult);

                return caseResult.IsSuccess
                    ? Result.Ok(Unit.Value)
                    : Result.Fail<Unit>(caseResult.Error ?? Error.Unspecified());
            });

            return channelCase.WithPriority(priority);
        }

        private ChannelCase CreateDefaultCase(Func<CancellationToken, Task<Result<TResult>>> onDefault, TaskCompletionSource<Result<TResult>> completion, int priority)
        {
            var defaultCase = ChannelCase.CreateDefault(async ct =>
            {
                Result<TResult> defaultResult;
                try
                {
                    defaultResult = await onDefault(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    defaultResult = Result.Fail<TResult>(Error.FromException(ex));
                }

                completion.TrySetResult(defaultResult);

                return defaultResult.IsSuccess
                    ? Result.Ok(Unit.Value)
                    : Result.Fail<Unit>(defaultResult.Error ?? Error.Unspecified());
            }, priority);

            return defaultCase;
        }
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

        var collected = new List<ChannelReader<T>>();
        foreach (var reader in sources)
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

        var active = new List<ChannelReader<T>>(readers);
        if (active.Count == 0)
        {
            return Result.Ok(Unit.Value);
        }

        while (active.Count > 0)
        {
            var cases = new ChannelCase[active.Count];
            for (var i = 0; i < active.Count; i++)
            {
                var reader = active[i];
                cases[i] = ChannelCase.Create(reader, onValue);
            }

            var iteration = timeout == Timeout.InfiniteTimeSpan
                ? await SelectAsync(provider, cancellationToken, cases).ConfigureAwait(false)
                : await SelectAsync(timeout, provider, cancellationToken, cases).ConfigureAwait(false);

            if (iteration.IsSuccess)
            {
                RemoveCompletedReaders(active);
                continue;
            }

            if (IsSelectDrained(iteration.Error))
            {
                await WaitForCompletionsAsync(active).ConfigureAwait(false);
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
            for (var index = list.Count - 1; index >= 0; index--)
            {
                if (list[index].Completion.IsCompleted)
                {
                    list.RemoveAt(index);
                }
            }
        }

        static Task WaitForCompletionsAsync(List<ChannelReader<T>> list)
        {
            if (list.Count == 0)
            {
                return Task.CompletedTask;
            }

            var tasks = new Task[list.Count];
            for (var i = 0; i < list.Count; i++)
            {
                tasks[i] = list[i].Completion;
            }

            return Task.WhenAll(tasks);
        }
    }

    private static async Task<Result<Unit>> FanInAsyncCore<T>(ChannelReader<T>[] readers, ChannelWriter<T> destination, bool completeDestination, TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
    {
        try
        {
            var result = await SelectFanInAsyncCore(readers, async (value, ct) =>
            {
                await destination.WriteAsync(value, ct).ConfigureAwait(false);
                return Result.Ok(Unit.Value);
            }, timeout, provider, cancellationToken).ConfigureAwait(false);
            if (completeDestination)
            {
                if (result.IsSuccess)
                {
                    destination.TryComplete();
                }
                else
                {
                    destination.TryComplete(CreateChannelOperationException(result.Error ?? Error.Unspecified()));
                }
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
                while (source.TryRead(out var item))
                {
                    var itemTimestamp = deadline == Timeout.InfiniteTimeSpan ? 0 : provider.GetTimestamp();

                    for (var i = 0; i < destinations.Count; i++)
                    {
                        var writeResult = await WriteWithDeadlineAsync(destinations[i], item, deadline, provider, itemTimestamp, cancellationToken).ConfigureAwait(false);
                        if (writeResult.IsFailure)
                        {
                            if (completeDestinations)
                            {
                                CompleteWriters(destinations, CreateChannelOperationException(writeResult.Error ?? Error.Unspecified()));
                            }

                            return writeResult;
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            var canceled = Error.Canceled(token: cancellationToken.CanBeCanceled ? cancellationToken : null);
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
            var fault = source.Completion.Exception?.GetBaseException() ?? new InvalidOperationException("Source channel faulted.");
            if (completeDestinations)
            {
                CompleteWriters(destinations, fault);
            }

            return Result.Fail<Unit>(Error.FromException(fault));
        }

        if (source.Completion.IsCanceled)
        {
            var canceled = Error.Canceled();
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
                var canWrite = await writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false);
                if (!canWrite)
                {
                    return Result.Fail<Unit>(Error.From("Destination channel has completed.", ErrorCodes.ChannelCompleted));
                }
            }
            else
            {
                var elapsed = provider.GetElapsedTime(startTimestamp);
                var remaining = deadline - elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return Result.Fail<Unit>(Error.Timeout(deadline));
                }

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var waitTask = writer.WaitToWriteAsync(linkedCts.Token).AsTask();
                var delayTask = provider.DelayAsync(remaining, linkedCts.Token);

                var completed = await Task.WhenAny(waitTask, delayTask).ConfigureAwait(false);
                if (completed == delayTask)
                {
                    linkedCts.Cancel();
                    return Result.Fail<Unit>(Error.Timeout(deadline));
                }

                var canWrite = await waitTask.ConfigureAwait(false);
                if (!canWrite)
                {
                    linkedCts.Cancel();
                    return Result.Fail<Unit>(Error.From("Destination channel has completed.", ErrorCodes.ChannelCompleted));
                }

                linkedCts.Cancel();
            }

            if (writer.TryWrite(value))
            {
                return Result.Ok(Unit.Value);
            }
        }
    }

    private static void CompleteWriters<T>(IReadOnlyList<ChannelWriter<T>> writers, Exception? exception)
    {
        for (var i = 0; i < writers.Count; i++)
        {
            if (exception is null)
            {
                writers[i].TryComplete();
                continue;
            }

            writers[i].TryComplete(exception);
        }
    }

    private static bool IsSelectDrained(Error? error) => error is { Code: ErrorCodes.SelectDrained };

    private static Exception CreateChannelOperationException(Error error)
    {
        if (error.Code == ErrorCodes.Canceled)
        {
            var message = error.Message ?? "Operation was canceled.";
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
            var options = new BoundedChannelOptions(capacity.Value)
            {
                FullMode = fullMode,
                SingleReader = singleReader,
                SingleWriter = singleWriter
            };

            return Channel.CreateBounded<T>(options);
        }

        var unboundedOptions = new UnboundedChannelOptions
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
        var options = new PrioritizedChannelOptions
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
