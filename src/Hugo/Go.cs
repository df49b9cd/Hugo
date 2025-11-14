using System.Diagnostics.CodeAnalysis;

namespace Hugo;

/// <summary>
/// Provides a collection of static helper methods that emulate core Go language features.
/// For best results, add `using static Hugo.Go;` to your file.
/// </summary>
public static partial class Go
{
    private const TaskCreationOptions DefaultRunOptions = TaskCreationOptions.DenyChildAttach;

    /// <summary>Gets a canonical cancellation error result used by Go helpers.</summary>
    public static readonly Error CancellationError = Error.Canceled();

    /// <summary>Creates a deferred action that runs when the returned handle is disposed.</summary>
    /// <param name="action">The callback to invoke during disposal.</param>
    /// <returns>A disposable wrapper that executes the provided action.</returns>
    public static Defer Defer(Action action) => new(action);

    /// <summary>
    /// Runs a function on a background scheduler. For tasks that should be tracked by a WaitGroup,
    /// prefer using the `wg.Go(...)` overloads for a cleaner syntax.
    /// </summary>
    /// <param name="func">The asynchronous delegate to execute.</param>
    /// <param name="scheduler">The scheduler used for dispatch. Defaults to <see cref="TaskScheduler.Default"/>.</param>
    /// <param name="creationOptions">Task creation flags applied to the scheduled work. <see cref="TaskCreationOptions.DenyChildAttach"/> is always enforced.</param>
    /// <returns>The task representing the queued work.</returns>
    public static Task Run(Func<Task> func, TaskScheduler? scheduler = null, TaskCreationOptions creationOptions = DefaultRunOptions)
    {
        ArgumentNullException.ThrowIfNull(func);
        return Run(_ => func(), CancellationToken.None, scheduler, creationOptions);
    }

    /// <summary>
    /// Runs a cancelable function on a background scheduler.
    /// </summary>
    /// <param name="func">The asynchronous delegate to execute.</param>
    /// <param name="cancellationToken">The token used to cancel the queued work.</param>
    /// <param name="scheduler">The scheduler used for dispatch. Defaults to <see cref="TaskScheduler.Default"/>.</param>
    /// <param name="creationOptions">Task creation flags applied to the scheduled work. <see cref="TaskCreationOptions.DenyChildAttach"/> is always enforced.</param>
    /// <returns>The task representing the queued work.</returns>
    [SuppressMessage("Design", "CA1068:CancellationTokenParametersShouldComeLast", Justification = "Scheduler and creation options must remain optional parameters for backward compatibility.")]
    public static Task Run(
        Func<CancellationToken, Task> func,
        CancellationToken cancellationToken = default,
        TaskScheduler? scheduler = null,
        TaskCreationOptions creationOptions = DefaultRunOptions)
    {
        ArgumentNullException.ThrowIfNull(func);
        return ScheduleAsync(func, cancellationToken, scheduler, creationOptions);
    }

    /// <summary>
    /// Reuses an already created <see cref="Task"/> without wrapping it in <see cref="Task.Run(Action)"/>.
    /// </summary>
    /// <param name="task">The task to surface.</param>
    /// <returns>The supplied <paramref name="task"/>.</returns>
    public static Task Run(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task;
    }

    /// <summary>
    /// Reuses an already created <see cref="Task{TResult}"/> without wrapping it in <see cref="Task.Run(Action)"/>.
    /// </summary>
    /// <param name="task">The task to surface.</param>
    /// <typeparam name="T">The result type of the task.</typeparam>
    /// <returns>The supplied <paramref name="task"/>.</returns>
    public static Task<T> Run<T>(Task<T> task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return task;
    }

    /// <summary>
    /// Converts a <see cref="ValueTask"/> to a <see cref="Task"/> so callers can track existing work without allocating a new <see cref="Task"/>.
    /// </summary>
    /// <param name="task">The value task to observe.</param>
    /// <returns>A task that completes when <paramref name="task"/> does.</returns>
    public static Task Run(ValueTask task) => task.IsCompletedSuccessfully ? Task.CompletedTask : task.AsTask();

    /// <summary>
    /// Converts a <see cref="ValueTask{TResult}"/> to a <see cref="Task{TResult}"/> so callers can track existing work without re-scheduling it.
    /// </summary>
    /// <param name="task">The value task to observe.</param>
    /// <typeparam name="T">The result type of the task.</typeparam>
    /// <returns>A task that completes when <paramref name="task"/> does.</returns>
    public static Task<T> Run<T>(ValueTask<T> task) => task.AsTask();

    /// <summary>Creates a successful result with the supplied payload.</summary>
    /// <typeparam name="T">The type of value captured by the result.</typeparam>
    /// <param name="value">The value to wrap in a success result.</param>
    /// <returns>A successful result containing <paramref name="value"/>.</returns>
    public static Result<T> Ok<T>(T value) => Result.Ok(value);

    /// <summary>Creates a failure result from an error instance.</summary>
    /// <typeparam name="T">The type carried by the result.</typeparam>
    /// <param name="error">The error describing the failure.</param>
    /// <returns>A failure result containing <paramref name="error"/>.</returns>
    public static Result<T> Err<T>(Error? error) => Result.Fail<T>(error ?? Error.Unspecified());

    /// <summary>Creates a failure result from an error message.</summary>
    /// <typeparam name="T">The type carried by the result.</typeparam>
    /// <param name="message">The error message.</param>
    /// <param name="code">An optional error code.</param>
    /// <returns>A failure result containing the generated error.</returns>
    public static Result<T> Err<T>(string message, string? code = null) =>
        Result.Fail<T>(Error.From(message, code));

    /// <summary>Creates a failure result from an exception.</summary>
    /// <typeparam name="T">The type carried by the result.</typeparam>
    /// <param name="exception">The exception to convert into an error.</param>
    /// <param name="code">An optional error code.</param>
    /// <returns>A failure result containing the generated error.</returns>
    public static Result<T> Err<T>(Exception exception, string? code = null) =>
        Result.Fail<T>(Error.FromException(exception, code));

    [SuppressMessage("Design", "CA1068:CancellationTokenParametersShouldComeLast", Justification = "Scheduler and creation options must remain optional parameters for backward compatibility.")]
    private static Task ScheduleAsync(
        Func<CancellationToken, Task> func,
        CancellationToken cancellationToken,
        TaskScheduler? scheduler,
        TaskCreationOptions creationOptions)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled(cancellationToken);
        }

        var options = NormalizeOptions(creationOptions);
        var taskScheduler = scheduler ?? TaskScheduler.Default;

        return Task.Factory
            .StartNew(
                static state =>
                {
                    var (callback, token) = ((Func<CancellationToken, Task> Callback, CancellationToken Token))state!;
                    return callback(token);
                },
                (func, cancellationToken),
                cancellationToken,
                options,
                taskScheduler)
            .Unwrap();
    }

    private static TaskCreationOptions NormalizeOptions(TaskCreationOptions creationOptions) =>
        creationOptions.HasFlag(TaskCreationOptions.DenyChildAttach)
            ? creationOptions
            : creationOptions | TaskCreationOptions.DenyChildAttach;
}
