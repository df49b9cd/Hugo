namespace Hugo;

/// <summary>
/// Provides a collection of static helper methods that emulate core Go language features.
/// For best results, add `using static Hugo.Go;` to your file.
/// </summary>
public static partial class Go
{
    /// <summary>Gets a canonical cancellation error result used by Go helpers.</summary>
    public static readonly Error CancellationError = Error.Canceled();

    /// <summary>Creates a deferred action that runs when the returned handle is disposed.</summary>
    /// <param name="action">The callback to invoke during disposal.</param>
    /// <returns>A disposable wrapper that executes the provided action.</returns>
    public static Defer Defer(Action action) => new(action);

    /// <summary>
    /// Runs a function on a background thread. For tasks that should be tracked by a WaitGroup,
    /// prefer using the `wg.Go(...)` extension method for a cleaner syntax.
    /// </summary>
    /// <param name="func">The asynchronous delegate to execute.</param>
    /// <returns>The task representing the queued work.</returns>
    public static Task Run(Func<Task> func) => func is null ? throw new ArgumentNullException(nameof(func)) : Task.Run(func);

    /// <summary>
    /// Runs a cancelable function on a background thread.
    /// </summary>
    /// <param name="func">The asynchronous delegate to execute.</param>
    /// <param name="cancellationToken">The token used to cancel the queued work.</param>
    /// <returns>The task representing the queued work.</returns>
    public static Task Run(Func<CancellationToken, Task> func, CancellationToken cancellationToken = default) =>
        func is null
            ? throw new ArgumentNullException(nameof(func))
            : Task.Run(() => func(cancellationToken), cancellationToken);

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
}
