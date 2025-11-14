namespace Hugo.TaskQueues.Diagnostics;

/// <summary>
/// Describes the context provided to TaskQueue metric tag enrichers.
/// </summary>
/// <param name="QueueName">Name assigned to the queue that produced the measurement.</param>
public readonly record struct TaskQueueTagContext(string? QueueName);
