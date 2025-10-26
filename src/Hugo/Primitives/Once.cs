namespace Hugo;

/// <summary>
/// A Once is an object that will perform an action exactly once.
/// </summary>
public sealed class Once
{
    private int _done;
    private readonly Lock _lock = new();

    /// <summary>Invokes the supplied action exactly once, ignoring subsequent invocations.</summary>
    /// <param name="action">The action to execute.</param>
    public void Do(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Volatile.Read(ref _done) == 1)
        {
            return;
        }

        lock (_lock)
        {
            if (_done == 0)
            {
                try
                {
                    action();
                }
                finally
                {
                    Volatile.Write(ref _done, 1);
                }
            }
        }
    }
}
