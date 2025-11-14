using Xunit;

namespace Hugo.Tests.TaskQueues;

[CollectionDefinition("TaskQueueConcurrency", DisableParallelization = true)]
public sealed class TaskQueueTestsCollection
{
}
