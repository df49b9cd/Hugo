using System;
using System.Threading;
using System.Threading.Tasks;

namespace Hugo;

internal static class TimeProviderExtensions
{
    public static Task DelayAsync(this TimeProvider provider, TimeSpan delay, CancellationToken cancellationToken = default)
    {
        if (provider is null)
            throw new ArgumentNullException(nameof(provider));

        if (delay == Timeout.InfiniteTimeSpan)
            return Task.Delay(Timeout.Infinite, cancellationToken);

        var state = new DelayState(provider, delay, cancellationToken);
        state.Initialize();
        return state.Task;
    }

    private sealed class DelayState
    {
        private readonly TimeProvider _provider;
        private readonly TimeSpan _dueTime;
        private readonly CancellationToken _cancellationToken;
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private ITimer? _timer;
        private CancellationTokenRegistration _registration;

        public DelayState(TimeProvider provider, TimeSpan dueTime, CancellationToken cancellationToken)
        {
            _provider = provider;
            _dueTime = dueTime;
            _cancellationToken = cancellationToken;
        }

        public Task Task => _tcs.Task;

        public void Initialize()
        {
            _timer = _provider.CreateTimer(static state => ((DelayState)state!).OnTimer(), this, _dueTime, Timeout.InfiniteTimeSpan);
            _registration = _cancellationToken.Register(static state => ((DelayState)state!).OnCanceled(), this);
        }

        private void OnTimer()
        {
            Cleanup();
            _tcs.TrySetResult();
        }

        private void OnCanceled()
        {
            Cleanup();
            _tcs.TrySetCanceled(_cancellationToken);
        }

        private void Cleanup()
        {
            _registration.Dispose();
            _registration = default;

            if (_timer is { } timer)
            {
                _timer = null;
                timer.Dispose();
            }
        }
    }
}
