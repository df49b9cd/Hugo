using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Sources;

namespace Hugo;

internal static class TimeProviderDelay
{
    public static ValueTask<bool> WaitAsync(TimeProvider provider, TimeSpan dueTime, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<bool>(cancellationToken);
        }

        if (dueTime <= TimeSpan.Zero)
        {
            return ValueTask.FromResult(true);
        }

        if (dueTime == Timeout.InfiniteTimeSpan)
        {
            return AwaitInfiniteDelay(cancellationToken);
        }

        return new TimerDelay(provider, dueTime, cancellationToken).Task;
    }

    private static async ValueTask<bool> AwaitInfiniteDelay(CancellationToken token)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        return true;
    }

    private sealed class TimerDelay : IValueTaskSource<bool>
    {
        private ManualResetValueTaskSourceCore<bool> _core;
        private readonly CancellationToken _token;
        private readonly CancellationTokenRegistration _registration;
        private readonly ITimer _timer;
        private int _completed;

        internal TimerDelay(TimeProvider provider, TimeSpan dueTime, CancellationToken token)
        {
            _token = token;
            _core.RunContinuationsAsynchronously = true;
            _core.Reset();
            _registration = token.Register(static state => ((TimerDelay)state!).OnCanceled(), this);
            _timer = provider.CreateTimer(static state => ((TimerDelay)state!).OnElapsed(), this, dueTime, Timeout.InfiniteTimeSpan);
        }

        internal ValueTask<bool> Task => new(this, _core.Version);

        private void OnElapsed()
        {
            if (!TryComplete())
            {
                return;
            }

            _core.SetResult(true);
        }

        private void OnCanceled()
        {
            if (!TryComplete())
            {
                return;
            }

            _core.SetException(new OperationCanceledException(_token));
        }

        private bool TryComplete()
        {
            if (Interlocked.Exchange(ref _completed, 1) == 1)
            {
                return false;
            }

            _timer.Dispose();
            _registration.Dispose();
            return true;
        }

        bool IValueTaskSource<bool>.GetResult(short token) => _core.GetResult(token);

        ValueTaskSourceStatus IValueTaskSource<bool>.GetStatus(short token) => _core.GetStatus(token);

        void IValueTaskSource<bool>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
            _core.OnCompleted(continuation, state, token, flags);
    }
}
