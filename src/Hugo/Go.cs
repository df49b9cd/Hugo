using System.Threading.Channels;

namespace Hugo;

/// <summary>
/// Provides a collection of static helper methods that emulate core Go language features.
/// For best results, add `using static Hugo.Go;` to your file.
/// </summary>
public static class Go
{
    public static readonly Error CancellationError = new("Operation was canceled.");

    public static Defer Defer(Action action) => new(action);

    /// <summary>
    /// Runs a function on a background thread. For tasks that should be tracked by a WaitGroup,
    /// prefer using the `wg.Go(...)` extension method for a cleaner syntax.
    /// </summary>
    public static void Run(Func<Task> func) => Task.Run(func);

    public static Channel<T> MakeChannel<T>(int? capacity = null) =>
        capacity > 0
            ? Channel.CreateBounded<T>(
                new BoundedChannelOptions(capacity.Value) { FullMode = BoundedChannelFullMode.Wait }
            )
            : Channel.CreateUnbounded<T>();

    public static (T Value, Error? Err) Ok<T>(T value) => (value, null);

    public static (T Value, Error? Err) Err<T>(Error? error) =>
        (default!, error ?? new Error("An unspecified error occurred."));

    public static (T Value, Error? Err) Err<T>(string message) => (default!, new Error(message));
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
        wg.Add(Task.Run(func));
    }
}
