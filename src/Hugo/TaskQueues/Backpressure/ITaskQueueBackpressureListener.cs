namespace Hugo.TaskQueues.Backpressure;

/// <summary>
/// Represents a component that reacts to <see cref="TaskQueueBackpressureSignal"/> instances.
/// </summary>
public interface ITaskQueueBackpressureListener
{
    /// <summary>
    /// Handles an emitted backpressure signal.
    /// </summary>
    /// <param name="signal">The signal being delivered.</param>
    /// <param name="cancellationToken">Token used to observe cancellation.</param>
    /// <returns>A task that completes when the listener finishes processing the signal.</returns>
    ValueTask OnSignalAsync(TaskQueueBackpressureSignal signal, CancellationToken cancellationToken = default);
}
