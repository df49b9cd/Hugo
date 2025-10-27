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

    /// <summary>Enters the synchronous read scope, blocking writers until the returned disposable is released.</summary>
    /// <returns>A disposable scope that exits the read lock when disposed.</returns>
    public IDisposable EnterReadScope()
    {
        ThrowIfDisposed();
        _sync.EnterReadLock();
        return new ReadScope(_sync);
    }

    /// <summary>Enters the synchronous write scope, blocking other readers and writers until the returned disposable is released.</summary>
    /// <returns>A disposable scope that exits the write lock when disposed.</returns>
    public IDisposable EnterWriteScope()
    {
        ThrowIfDisposed();
        _sync.EnterWriteLock();
        return new WriteScope(_sync);
    }

    /// <summary>
    /// Asynchronously acquires a read lock, honoring <paramref name="cancellationToken"/>, and returns a releaser that must be disposed to exit the read scope.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the wait.</param>
    /// <returns>An asynchronous releaser that exits the read scope when disposed.</returns>
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

    /// <summary>
    /// Asynchronously acquires the write lock, honoring <paramref name="cancellationToken"/>, and returns a releaser that must be disposed to exit the write scope.
    /// </summary>
    /// <param name="cancellationToken">The token used to cancel the wait.</param>
    /// <returns>An asynchronous releaser that exits the write scope when disposed.</returns>
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

    /// <summary>Releases unmanaged resources.</summary>
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

    public readonly struct AsyncReadReleaser(RwMutex mutex) : IAsyncDisposable, IDisposable, IEquatable<AsyncReadReleaser>
    {
        private readonly RwMutex _mutex = mutex;

        /// <summary>Exits the asynchronous read scope.</summary>
        public void Dispose() => _mutex.ReleaseReader();

        /// <summary>Exits the asynchronous read scope.</summary>
        /// <returns>A completed task.</returns>
        public ValueTask DisposeAsync()
        {
            _mutex.ReleaseReader();
            return ValueTask.CompletedTask;
        }

        public override bool Equals(object? obj) => obj is AsyncReadReleaser other && Equals(other);

        public override int GetHashCode() => _mutex?.GetHashCode() ?? 0;

        public static bool operator ==(AsyncReadReleaser left, AsyncReadReleaser right) => left.Equals(right);

        public static bool operator !=(AsyncReadReleaser left, AsyncReadReleaser right) => !left.Equals(right);

        public bool Equals(AsyncReadReleaser other) => ReferenceEquals(_mutex, other._mutex);
    }

    public readonly struct AsyncWriteReleaser(RwMutex mutex) : IAsyncDisposable, IDisposable, IEquatable<AsyncWriteReleaser>
    {
        private readonly RwMutex _mutex = mutex;

        /// <summary>Exits the asynchronous write scope.</summary>
        public void Dispose() => _mutex.ReleaseWriter();

        /// <summary>Exits the asynchronous write scope.</summary>
        /// <returns>A completed task.</returns>
        public ValueTask DisposeAsync()
        {
            _mutex.ReleaseWriter();
            return ValueTask.CompletedTask;
        }

        public override bool Equals(object? obj) => obj is AsyncWriteReleaser other && Equals(other);

        public override int GetHashCode() => _mutex?.GetHashCode() ?? 0;

        public static bool operator ==(AsyncWriteReleaser left, AsyncWriteReleaser right) => left.Equals(right);

        public static bool operator !=(AsyncWriteReleaser left, AsyncWriteReleaser right) => !left.Equals(right);

        public bool Equals(AsyncWriteReleaser other) => ReferenceEquals(_mutex, other._mutex);
    }
}
