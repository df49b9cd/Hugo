using System.Threading.Channels;
using System.Threading;

namespace Hugo;

internal sealed class TimerChannel : IAsyncDisposable, IDisposable
{
    private static readonly TimerCallback TickCallback = static state => ((TimerChannel)state!).OnTick();
    private static readonly TimeSpan Immediate = TimeSpan.Zero;

    private readonly TimeProvider _provider;
    private readonly Channel<DateTimeOffset> _channel;
    private readonly TimeSpan _dueTime;
    private readonly TimeSpan _period;
    private readonly bool _singleShot;
    private readonly CancellationToken _cancellationToken;

    private CancellationTokenRegistration _cancellationRegistration;
    private ITimer? _timer;
    private int _completed;

    private TimerChannel(TimeProvider provider, TimeSpan dueTime, TimeSpan period, bool singleShot, CancellationToken cancellationToken)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _dueTime = dueTime;
        _period = period;
        _singleShot = singleShot;
        _cancellationToken = cancellationToken;

        var options = new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = false
        };

        _channel = Channel.CreateBounded<DateTimeOffset>(options);
    }

    public ChannelReader<DateTimeOffset> Reader => _channel.Reader;

    public static TimerChannel Start(TimeProvider provider, TimeSpan dueTime, TimeSpan period, CancellationToken cancellationToken, bool singleShot)
    {
        var channel = new TimerChannel(provider, dueTime, period, singleShot, cancellationToken);
        channel.Initialize();
        return channel;
    }

    public ValueTask<DateTimeOffset> ReadAsync(CancellationToken cancellationToken = default) => _channel.Reader.ReadAsync(cancellationToken);

    public bool TryRead(out DateTimeOffset value) => _channel.Reader.TryRead(out value);

    public void Dispose()
    {
        Complete();
        GC.SuppressFinalize(this);
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        return ValueTask.CompletedTask;
    }

    private void Initialize()
    {
    _cancellationRegistration = _cancellationToken.Register(static state => ((TimerChannel)state!).OnCanceled(), this);

        if (_singleShot && _dueTime <= Immediate)
        {
            PublishTick();
            Complete();
            return;
        }

        var period = _singleShot ? Timeout.InfiniteTimeSpan : _period;
        var dueTime = _dueTime <= Immediate ? Immediate : _dueTime;

        _timer = _provider.CreateTimer(TickCallback, this, dueTime, period);
    }

    private void OnTick()
    {
        if (Volatile.Read(ref _completed) == 1)
            return;

        PublishTick();

        if (_singleShot)
        {
            Complete();
        }
    }

    private void PublishTick()
    {
        if (Volatile.Read(ref _completed) == 1)
            return;

        var timestamp = _provider.GetUtcNow();
        _channel.Writer.TryWrite(timestamp);
    }

    private void OnCanceled()
    {
        Complete(new OperationCanceledException(_cancellationToken));
    }

    private void Complete(Exception? error = null)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 1)
            return;

        _cancellationRegistration.Dispose();

        var timer = Interlocked.Exchange(ref _timer, null);
        timer?.Dispose();

        if (error is null)
        {
            _channel.Writer.TryComplete();
        }
        else
        {
            _channel.Writer.TryComplete(error);
        }
    }
}
