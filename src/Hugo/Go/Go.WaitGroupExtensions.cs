namespace Hugo;

/// <summary>
/// Provides extension methods for the Go# framework.
/// </summary>
public static class GoWaitGroupExtensions
{
    /// <param name="wg">The WaitGroup to add the task to.</param>
    extension(WaitGroup wg)
    {
        /// <summary>
        /// Runs a function on a background thread and adds it to the WaitGroup, similar to `go func()`.
        /// This is the recommended way to run concurrent tasks that need to be awaited.
        /// </summary>
        /// <param name="func">The async function to execute.</param>
        /// <example>wg.Go(async () => { ... });</example>
        public void Go(Func<ValueTask> func, TaskScheduler? scheduler = null, TaskCreationOptions creationOptions = TaskCreationOptions.DenyChildAttach)
        {
            ArgumentNullException.ThrowIfNull(wg);
            ArgumentNullException.ThrowIfNull(func);

            wg.Go(func, scheduler, creationOptions, cancellationToken: default);
        }

        /// <summary>
        /// Runs a cancellation-aware function on a background thread and adds it to the WaitGroup.
        /// </summary>
        /// <param name="func"></param>
        /// <param name="scheduler">Optional scheduler used for dispatch.</param>
        /// <param name="creationOptions">Task creation flags applied to the scheduled work.</param>
        /// <param name="cancellationToken"></param>
        public void Go(
            Func<CancellationToken, ValueTask> func, TaskScheduler? scheduler = null,
            TaskCreationOptions creationOptions = TaskCreationOptions.DenyChildAttach,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(wg);
            ArgumentNullException.ThrowIfNull(func);

            wg.Go(() => func(cancellationToken), scheduler, creationOptions, cancellationToken);
        }
    }
}
