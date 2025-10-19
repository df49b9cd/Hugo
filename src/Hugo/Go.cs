using System.Threading.Channels;
using Hugo.Primitives;

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
        SelectInternalAsync(cases, Timeout.InfiniteTimeSpan, provider, cancellationToken);

    /// <summary>
    /// Awaits the first channel case to produce a value or returns when the timeout elapses.
    /// </summary>
    public static Task<Result<Unit>> SelectAsync(TimeSpan timeout, TimeProvider? provider = null, CancellationToken cancellationToken = default, params ChannelCase[] cases) =>
        SelectInternalAsync(cases, timeout, provider, cancellationToken);

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

    private static async Task<Result<Unit>> SelectInternalAsync(ChannelCase[] cases, TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cases);

        if (cases.Length == 0)
        {
            throw new ArgumentException("At least one channel case must be provided.", nameof(cases));
        }

        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout));
        }

    provider ??= TimeProvider.System;
    using var activity = GoDiagnostics.StartSelectActivity(cases.Length, timeout);
    GoDiagnostics.RecordChannelSelectAttempt(activity);
    var startTimestamp = provider.GetTimestamp();

    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    var caseList = new List<ChannelCase>(cases.Length);
    var waitTasks = new List<Task<(bool HasValue, object? Value)>>(cases.Length);
    var taskIndex = new Dictionary<Task, int>(cases.Length);

        for (var i = 0; i < cases.Length; i++)
        {
            var channelCase = cases[i];
            var waitTask = channelCase.WaitAsync(linkedCts.Token);
            caseList.Add(channelCase);
            waitTasks.Add(waitTask);
            taskIndex[waitTask] = i;
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

                if (!taskIndex.TryGetValue(completedTask, out var index))
                {
                    continue;
                }

                var waitTask = waitTasks[index];
                taskIndex.Remove(waitTask);

                var (hasValue, value) = await waitTask.ConfigureAwait(false);
                if (!hasValue)
                {
                    caseList.RemoveAt(index);
                    waitTasks.RemoveAt(index);
                    RebuildIndex(taskIndex, waitTasks);
                    continue;
                }

                linkedCts.Cancel();
                var completionDuration = provider.GetElapsedTime(startTimestamp);

                try
                {
                    var continuationResult = await caseList[index].ContinueWithAsync(value, cancellationToken).ConfigureAwait(false);
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

        static void RebuildIndex(Dictionary<Task, int> map, List<Task<(bool HasValue, object? Value)>> tasks)
        {
            map.Clear();
            for (var i = 0; i < tasks.Count; i++)
            {
                map[tasks[i]] = i;
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
                    output.Writer.TryComplete(CreateFanInException(result.Error ?? Error.Unspecified()));
                }
            }
            catch (Exception ex)
            {
                output.Writer.TryComplete(ex);
            }
        }, CancellationToken.None);

        return output.Reader;
    }


    public sealed class SelectBuilder<TResult>
    {
        private readonly List<Func<TaskCompletionSource<Result<TResult>>, ChannelCase>> _caseFactories = [];
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

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<Result<TResult>>> onValue)
        {
            ArgumentNullException.ThrowIfNull(reader);

            ArgumentNullException.ThrowIfNull(onValue);

            _caseFactories.Add(completion => CreateCase(reader, onValue, completion));
            return this;
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, Task<Result<TResult>>> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, (value, _) => onValue(value));
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<TResult>> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, async (value, ct) => Result.Ok(await onValue(value, ct).ConfigureAwait(false)));
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, Task<TResult>> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, async (value, _) => Result.Ok(await onValue(value).ConfigureAwait(false)));
        }

        public SelectBuilder<TResult> Case<T>(ChannelReader<T> reader, Func<T, TResult> onValue)
        {
            ArgumentNullException.ThrowIfNull(onValue);

            return Case(reader, (value, _) => Task.FromResult(Result.Ok(onValue(value))));
        }

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, CancellationToken, Task<Result<TResult>>> onValue) =>
            Case(template.Reader, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, Task<Result<TResult>>> onValue) =>
            Case(template.Reader, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, CancellationToken, Task<TResult>> onValue) =>
            Case(template.Reader, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, Task<TResult>> onValue) =>
            Case(template.Reader, onValue);

        public SelectBuilder<TResult> Case<T>(ChannelCaseTemplate<T> template, Func<T, TResult> onValue) =>
            Case(template.Reader, onValue);

        public async Task<Result<TResult>> ExecuteAsync()
        {
            if (_caseFactories.Count == 0)
            {
                throw new InvalidOperationException("At least one channel case must be configured before executing the select.");
            }

            var completion = new TaskCompletionSource<Result<TResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var cases = new ChannelCase[_caseFactories.Count];
            for (var i = 0; i < _caseFactories.Count; i++)
            {
                cases[i] = _caseFactories[i](completion);
            }

            var selectResult = await Go.SelectAsync(_timeout, _provider, _cancellationToken, cases).ConfigureAwait(false);

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

        private ChannelCase CreateCase<T>(ChannelReader<T> reader, Func<T, CancellationToken, Task<Result<TResult>>> onValue, TaskCompletionSource<Result<TResult>> completion)
        {
            return ChannelCase.Create(reader, async (value, ct) =>
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
                    destination.TryComplete(CreateFanInException(result.Error ?? Error.Unspecified()));
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

    private static bool IsSelectDrained(Error? error) => error is { Code: ErrorCodes.SelectDrained };

    private static Exception CreateFanInException(Error error)
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
