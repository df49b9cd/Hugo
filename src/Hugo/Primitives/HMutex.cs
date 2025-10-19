namespace Hugo;

/// <summary>
/// Backward-compatible wrapper around <see cref="Mutex"/>.
/// </summary>
[Obsolete("HMutex is deprecated. Use Mutex instead.")]
public sealed class HMutex : IDisposable
{
    private readonly Mutex _inner = new();

    /// <summary>
    /// Enters the synchronous critical section, returning a scope that releases the lock when disposed.
    /// </summary>
    public Lock.Scope EnterScope() => _inner.EnterScope();

    /// <summary>
    /// Asynchronously waits for the mutex and returns a releaser that unlocks when disposed.
    /// </summary>
    public ValueTask<Mutex.AsyncLockReleaser> LockAsync(CancellationToken cancellationToken = default) =>
        _inner.LockAsync(cancellationToken);

    /// <summary>
    /// Releases unmanaged resources.
    /// </summary>
    public void Dispose() => _inner.Dispose();
}
