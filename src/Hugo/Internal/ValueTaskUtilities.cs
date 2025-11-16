using System.Threading.Tasks;

namespace Hugo;

internal static class ValueTaskUtilities
{
    public static ValueTask WhenAll(IReadOnlyList<ValueTask> operations)
    {
        if (operations.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var tasks = new Task[operations.Count];
        for (var i = 0; i < operations.Count; i++)
        {
            tasks[i] = operations[i].AsTask();
        }

        return new ValueTask(Task.WhenAll(tasks));
    }

    public static async ValueTask YieldAsync()
    {
        await Task.Yield();
    }

    public static async ValueTask<int> WhenAny(ValueTask first, ValueTask second)
    {
        var firstTask = first.AsTask();
        var secondTask = second.AsTask();
        var completed = await Task.WhenAny(firstTask, secondTask).ConfigureAwait(false);
        return ReferenceEquals(completed, firstTask) ? 0 : 1;
    }

    public static async ValueTask<int> WhenAny<TFirst>(ValueTask<TFirst> first, ValueTask second)
    {
        var firstTask = first.AsTask();
        var secondTask = second.AsTask();
        var completed = await Task.WhenAny(firstTask, secondTask).ConfigureAwait(false);
        return ReferenceEquals(completed, firstTask) ? 0 : 1;
    }

    public static async ValueTask<int> WhenAny<TFirst, TSecond>(ValueTask<TFirst> first, ValueTask<TSecond> second)
    {
        var firstTask = first.AsTask();
        var secondTask = second.AsTask();
        var completed = await Task.WhenAny(firstTask, secondTask).ConfigureAwait(false);
        return ReferenceEquals(completed, firstTask) ? 0 : 1;
    }
}
