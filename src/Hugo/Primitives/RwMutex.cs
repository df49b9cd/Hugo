namespace Hugo;

/// <summary>
/// Provides reader/writer mutual exclusion semantics with both synchronous and asynchronous APIs.
/// </summary>
public sealed class RwMutex : IDisposable
{
    private readonly ReaderWriterLockSlim _sync = new(LockRecursionPolicy.NoRecursion);
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    private readonly SemaphoreSlim _writerGate = new(1, 1);
    private int _readerCount;
    private int _disposed;

    public IDisposable EnterReadScope()
    {
        ThrowIfDisposed();
        _sync.EnterReadLock();
        return new ReadScope(_sync);
    }

    public IDisposable EnterWriteScope()
    {
        ThrowIfDisposed();
        _sync.EnterWriteLock();
        return new WriteScope(_sync);
    }

    public async ValueTask<AsyncReadReleaser> RLockAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _readerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var readerRegistered = false;
        var writerGateAcquired = false;

        try
        {
            ThrowIfDisposed();
            var readers = Interlocked.Increment(ref _readerCount);
            readerRegistered = true;

            if (readers == 1)
            {
                await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                writerGateAcquired = true;
            }
        }
        catch
        {
            if (readerRegistered && Interlocked.Decrement(ref _readerCount) == 0 && writerGateAcquired)
            {
                _writerGate.Release();
            }

            _readerGate.Release();
            throw;
        }

        _readerGate.Release();
        return new AsyncReadReleaser(this);
    }

    public async ValueTask<AsyncWriteReleaser> LockAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _writerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new AsyncWriteReleaser(this);
    }

    private void ReleaseReader()
    {
        if (Interlocked.Decrement(ref _readerCount) == 0)
        {
            _writerGate.Release();
        }
    }

    private void ReleaseWriter() => _writerGate.Release();

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) == 1)
        {
            throw new ObjectDisposedException(nameof(RwMutex));
        }
    }

    /// <summary>
    /// Releases unmanaged resources.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _sync.Dispose();
        _readerGate.Dispose();
        _writerGate.Dispose();
    }

    private readonly struct ReadScope(ReaderWriterLockSlim rwLock) : IDisposable
    {
        private readonly ReaderWriterLockSlim _rwLock = rwLock;

        public void Dispose() => _rwLock.ExitReadLock();
    }

    private readonly struct WriteScope(ReaderWriterLockSlim rwLock) : IDisposable
    {
        private readonly ReaderWriterLockSlim _rwLock = rwLock;

        public void Dispose() => _rwLock.ExitWriteLock();
    }

    public readonly struct AsyncReadReleaser(RwMutex mutex) : IAsyncDisposable, IDisposable
    {
        private readonly RwMutex _mutex = mutex;

        public void Dispose() => _mutex.ReleaseReader();

        public ValueTask DisposeAsync()
        {
            _mutex.ReleaseReader();
            return ValueTask.CompletedTask;
        }
    }

    public readonly struct AsyncWriteReleaser(RwMutex mutex) : IAsyncDisposable, IDisposable
    {
        private readonly RwMutex _mutex = mutex;

        public void Dispose() => _mutex.ReleaseWriter();

        public ValueTask DisposeAsync()
        {
            _mutex.ReleaseWriter();
            return ValueTask.CompletedTask;
        }
    }
}
