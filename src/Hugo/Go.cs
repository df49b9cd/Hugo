namespace Hugo;

/// <summary>
/// Provides a collection of static helper methods that emulate core Go language features.
/// For best results, add `using static Hugo.Go;` to your file.
/// </summary>
public static partial class Go
{
    public static readonly Error CancellationError = Error.Canceled();

    public static Defer Defer(Action action) => new(action);

    /// <summary>
    /// Runs a function on a background thread. For tasks that should be tracked by a WaitGroup,
    /// prefer using the `wg.Go(...)` extension method for a cleaner syntax.
    /// </summary>
    public static Task Run(Func<Task> func) => func is null ? throw new ArgumentNullException(nameof(func)) : Task.Run(func);

    /// <summary>
    /// Runs a cancelable function on a background thread.
    /// </summary>
    public static Task Run(Func<CancellationToken, Task> func, CancellationToken cancellationToken = default) =>
        func is null
            ? throw new ArgumentNullException(nameof(func))
            : Task.Run(() => func(cancellationToken), cancellationToken);

    public static Result<T> Ok<T>(T value) => Result.Ok(value);

    public static Result<T> Err<T>(Error? error) => Result.Fail<T>(error ?? Error.Unspecified());

    public static Result<T> Err<T>(string message, string? code = null) =>
        Result.Fail<T>(Error.From(message, code));

    public static Result<T> Err<T>(Exception exception, string? code = null) =>
        Result.Fail<T>(Error.FromException(exception, code));
}
