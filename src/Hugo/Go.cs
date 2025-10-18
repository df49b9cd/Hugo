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
            throw new ArgumentOutOfRangeException(nameof(delay));

        provider ??= TimeProvider.System;
        return provider.DelayAsync(delay, cancellationToken);
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

    private static async Task<Result<Unit>> SelectInternalAsync(ChannelCase[] cases, TimeSpan timeout, TimeProvider? provider, CancellationToken cancellationToken)
    {
        if (cases is null)
            throw new ArgumentNullException(nameof(cases));
        if (cases.Length == 0)
            throw new ArgumentException("At least one channel case must be provided.", nameof(cases));
        if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        provider ??= TimeProvider.System;
        GoDiagnostics.RecordChannelSelectAttempt();
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
                    GoDiagnostics.RecordChannelSelectCompleted(provider.GetElapsedTime(startTimestamp));
                    return Result.Fail<Unit>(Error.From("All channel cases completed without yielding a value.", ErrorCodes.Unspecified));
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
                        GoDiagnostics.RecordChannelSelectTimeout(provider.GetElapsedTime(startTimestamp));
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
                var duration = provider.GetElapsedTime(startTimestamp);
                GoDiagnostics.RecordChannelSelectCompleted(duration);

                try
                {
                    return await caseList[index].ContinueWithAsync(value, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return Result.Fail<Unit>(Error.FromException(ex));
                }
            }
        }
        catch (OperationCanceledException)
        {
            GoDiagnostics.RecordChannelSelectCanceled(provider.GetElapsedTime(startTimestamp));
            throw;
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

    public readonly record struct Unit
    {
        public static readonly Unit Value = new();
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
            options.DefaultPriority = defaultPriority.Value;

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
        if (wg is null)
            throw new ArgumentNullException(nameof(wg));

        if (func is null)
            throw new ArgumentNullException(nameof(func));

        wg.Add(Task.Run(func));
    }

    /// <summary>
    /// Runs a cancellation-aware function on a background thread and adds it to the WaitGroup.
    /// </summary>
    public static void Go(this WaitGroup wg, Func<CancellationToken, Task> func, CancellationToken cancellationToken)
    {
        if (wg is null)
            throw new ArgumentNullException(nameof(wg));

        if (func is null)
            throw new ArgumentNullException(nameof(func));

        wg.Go(() => func(cancellationToken), cancellationToken);
    }
}
