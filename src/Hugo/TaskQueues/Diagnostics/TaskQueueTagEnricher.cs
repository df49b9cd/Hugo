using System.Diagnostics;

namespace Hugo.TaskQueues.Diagnostics;

/// <summary>
/// Enriches TaskQueue metric tags before they are exported.
/// </summary>
/// <param name="context">Measurement context.</param>
/// <param name="tags">Tag list to mutate.</param>
public delegate void TaskQueueTagEnricher(in TaskQueueTagContext context, ref TagList tags);
