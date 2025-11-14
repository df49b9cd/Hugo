using System.Diagnostics.CodeAnalysis;

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
    public static void Go(this WaitGroup wg, Func<Task> func, TaskScheduler? scheduler = null, TaskCreationOptions creationOptions = TaskCreationOptions.DenyChildAttach)
    {
        ArgumentNullException.ThrowIfNull(wg);
        ArgumentNullException.ThrowIfNull(func);

        wg.Go(func, cancellationToken: default, scheduler, creationOptions);
    }

    /// <summary>
    /// Runs a cancellation-aware function on a background thread and adds it to the WaitGroup.
    /// </summary>
    /// <param name="scheduler">Optional scheduler used for dispatch.</param>
    /// <param name="creationOptions">Task creation flags applied to the scheduled work.</param>
    [SuppressMessage("Design", "CA1068:CancellationTokenParametersShouldComeLast", Justification = "Scheduler and creation options must remain optional parameters for backward compatibility.")]
    public static void Go(this WaitGroup wg, Func<CancellationToken, Task> func, CancellationToken cancellationToken, TaskScheduler? scheduler = null, TaskCreationOptions creationOptions = TaskCreationOptions.DenyChildAttach)
    {
        ArgumentNullException.ThrowIfNull(wg);
        ArgumentNullException.ThrowIfNull(func);

        wg.Go(() => func(cancellationToken), cancellationToken, scheduler, creationOptions);
    }
}
