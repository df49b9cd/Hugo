namespace Hugo;

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

    private static void Go(this WaitGroup wg, Func<Task> func, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(wg);
        ArgumentNullException.ThrowIfNull(func);

        wg.Add(Task.Run(() => func(), cancellationToken));
    }
}
