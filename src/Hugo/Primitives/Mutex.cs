namespace Hugo;

/// <summary>
/// Provides both synchronous and asynchronous mutual exclusion primitives.
/// </summary>
public sealed class Mutex : IDisposable
{
    private readonly SemaphoreSlim _asyncLock = new(1, 1);
    private readonly Lock _lock = new();
    private int _disposed;

    /// <summary>
    /// Enters the synchronous critical section, returning a scope that releases the lock when disposed.
    /// </summary>
    public Lock.Scope EnterScope()
    {
        ThrowIfDisposed();
        return _lock.EnterScope();
    }

    /// <summary>
    /// Asynchronously waits for the mutex and returns a releaser that unlocks when disposed.
    /// </summary>
    public async ValueTask<AsyncLockReleaser> LockAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _asyncLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new AsyncLockReleaser(_asyncLock);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) == 1, nameof(Mutex));

    /// <summary>
    /// Releases unmanaged resources.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _asyncLock.Dispose();
    }

    /// <summary>
    /// Releases the asynchronous lock when disposed.
    /// </summary>
    public struct AsyncLockReleaser : IAsyncDisposable, IDisposable
    {
        private SemaphoreSlim? _semaphore;

        internal AsyncLockReleaser(SemaphoreSlim semaphore)
        {
            _semaphore = semaphore ?? throw new ArgumentNullException(nameof(semaphore));
        }

        public void Dispose() => Release();

        public ValueTask DisposeAsync()
        {
            Release();
            return ValueTask.CompletedTask;
        }

        private void Release()
        {
            var semaphore = Interlocked.Exchange(ref _semaphore, null);
            semaphore?.Release();
        }
    }
}
