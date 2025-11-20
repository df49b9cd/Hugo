using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Hugo;

/// <summary>
/// Configuration options for <see cref="PrioritizedChannel{T}"/> instances.
/// </summary>
public sealed class PrioritizedChannelOptions
{
    private int _priorityLevels = 3;
    private int? _defaultPriority;
    private int _prefetchPerPriority = 1;

    /// <summary>
    /// Gets or sets the number of priority levels supported by the channel. Lower indices represent higher priority.
    /// </summary>
    public int PriorityLevels
    {
        get => _priorityLevels;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);

            _priorityLevels = value;
        }
    }

    /// <summary>
    /// Gets or sets the default priority used by <see cref="PrioritizedChannel{T}.PrioritizedWriter"/> overloads that do not explicitly specify a priority.
    /// When not supplied, the lowest priority (PriorityLevels - 1) will be used.
    /// </summary>
    public int? DefaultPriority
    {
        get => _defaultPriority;
        set
        {
            if (value is null)
            {
                _defaultPriority = null;
                return;
            }

            if (value < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _defaultPriority = value;
        }
    }

    /// <summary>
    /// Gets or sets the bounded capacity per priority level. When <c>null</c>, the channel levels are unbounded.
    /// </summary>
    public int? CapacityPerLevel
    {
        get => _capacityPerLevel;
        set
        {
            if (value is null)
            {
                _capacityPerLevel = null;
                return;
            }

            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            _capacityPerLevel = value;
        }
    }

    private int? _capacityPerLevel;

    /// <summary>
    /// Gets or sets the maximum number of prefetched items staged per priority before readers consume them.
    /// </summary>
    public int PrefetchPerPriority
    {
        get => _prefetchPerPriority;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value);
            _prefetchPerPriority = value;
        }
    }

    /// <summary>
    /// Gets or sets the bounded channel full mode applied when <see cref="CapacityPerLevel"/> is specified.
    /// </summary>
    public BoundedChannelFullMode FullMode { get; set; } = BoundedChannelFullMode.Wait;

    /// <summary>
    /// Gets or sets a value indicating whether only a single reader will access the channel.
    /// </summary>
    public bool SingleReader { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether only a single writer will access the channel.
    /// </summary>
    public bool SingleWriter { get; set; }
}

/// <summary>
/// Represents a channel that multiplexes multiple priority levels while presenting a single reader/writer façade.
/// </summary>
public sealed class PrioritizedChannel<T>
{
    private readonly PrioritizedChannelReader _reader;
    private readonly PrioritizedChannelWriter _writer;

    /// <summary>Initializes a new instance of the <see cref="PrioritizedChannel{T}"/> class.</summary>
    /// <param name="options">The channel configuration.</param>
    public PrioritizedChannel(PrioritizedChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var levels = options.PriorityLevels;
        if (options.DefaultPriority is { } defaultPriority && defaultPriority >= levels)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.DefaultPriority, "Default priority must be less than the number of priority levels.");
        }

        var capacity = options.CapacityPerLevel;
        var readers = new ChannelReader<T>[levels];
        var writers = new ChannelWriter<T>[levels];

        for (var i = 0; i < levels; i++)
        {
            Channel<T> channel;
            if (capacity is { } boundedCapacity)
            {
                var boundedOptions = new BoundedChannelOptions(boundedCapacity)
                {
                    FullMode = options.FullMode,
                    SingleReader = options.SingleReader,
                    SingleWriter = options.SingleWriter
                };
                channel = Channel.CreateBounded<T>(boundedOptions);
            }
            else
            {
                var unboundedOptions = new UnboundedChannelOptions
                {
                    SingleReader = options.SingleReader,
                    SingleWriter = options.SingleWriter,
                    AllowSynchronousContinuations = false
                };
                channel = Channel.CreateUnbounded<T>(unboundedOptions);
            }

            readers[i] = channel.Reader;
            writers[i] = channel.Writer;
        }

        var resolvedDefaultPriority = options.DefaultPriority ?? (levels - 1);
        _reader = new PrioritizedChannelReader(readers, options.PrefetchPerPriority);
        _writer = new PrioritizedChannelWriter(writers, resolvedDefaultPriority);
        PriorityLevels = levels;
        DefaultPriority = resolvedDefaultPriority;
        CapacityPerLevel = capacity;
        PrefetchPerPriority = options.PrefetchPerPriority;
    }

    /// <summary>
    /// Gets the number of priority levels supported by the channel.
    /// </summary>
    public int PriorityLevels { get; }

    /// <summary>
    /// Gets the default priority used when no priority is explicitly supplied by the writer.
    /// </summary>
    public int DefaultPriority { get; }

    /// <summary>
    /// Gets the bounded capacity per level, when applicable.
    /// </summary>
    public int? CapacityPerLevel { get; }

    /// <summary>
    /// Gets the number of items that may be prefetched per priority before the reader yields back pressure.
    /// </summary>
    public int PrefetchPerPriority { get; }

    /// <summary>
    /// Gets the reader associated with the channel.
    /// </summary>
    public ChannelReader<T> Reader => _reader;

    /// <summary>
    /// Gets the writer associated with the channel.
    /// </summary>
    public ChannelWriter<T> Writer => _writer;

    /// <summary>
    /// Gets the prioritized reader with priority-aware helpers.
    /// </summary>
    public PrioritizedChannelReader PrioritizedReader => _reader;

    /// <summary>
    /// Gets the prioritized writer with priority-aware helpers.
    /// </summary>
    public PrioritizedChannelWriter PrioritizedWriter => _writer;

    /// <summary>
    /// Reader implementation that honors priority ordering.
    /// </summary>
    public sealed class PrioritizedChannelReader : ChannelReader<T>
    {
        private readonly ChannelReader<T>[] _readers;
        private readonly ConcurrentQueue<T>[] _buffers;
        private readonly int[] _bufferedPerPriority;
        private readonly ChannelCase<Go.Unit>[] _laneCases;
        private readonly Task _completion;
        private readonly int _prefetchPerPriority;
        private readonly bool _singleLane;
        private int _bufferedTotal;

        internal PrioritizedChannelReader(ChannelReader<T>[] readers, int prefetchPerPriority)
        {
            _readers = readers ?? throw new ArgumentNullException(nameof(readers));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(prefetchPerPriority);

            _prefetchPerPriority = prefetchPerPriority;
            _singleLane = readers.Length == 1;
            _buffers = new ConcurrentQueue<T>[readers.Length];
            _bufferedPerPriority = new int[readers.Length];
            var completions = new Task[readers.Length];
            for (var i = 0; i < readers.Length; i++)
            {
                _buffers[i] = new ConcurrentQueue<T>();
                completions[i] = readers[i].Completion;
            }

            _laneCases = _singleLane ? Array.Empty<ChannelCase<Go.Unit>>() : BuildLaneCases(readers);
            _completion = _singleLane ? completions[0] : Task.WhenAll(completions);
        }

        /// <inheritdoc />
        public override Task Completion => _completion;

        internal int BufferedItemCount => Volatile.Read(ref _bufferedTotal);

        internal int GetBufferedCountForPriority(int priority)
        {
            if ((uint)priority >= (uint)_buffers.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(priority));
            }

            return Volatile.Read(ref _bufferedPerPriority[priority]);
        }

        /// <inheritdoc />
        public override bool TryRead(out T item)
        {
            if (TryReadFromBuffers(out item!))
            {
                return true;
            }

            for (var i = 0; i < _readers.Length; i++)
            {
                if (_readers[i].TryRead(out item!))
                {
                    return true;
                }
            }

            item = default!;
            return false;
        }

        /// <inheritdoc />
        public override ValueTask<T> ReadAsync(CancellationToken cancellationToken = default) => TryRead(out var item) ? new ValueTask<T>(item) : ReadSlowAsync(cancellationToken);

        private async ValueTask<T> ReadSlowAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var hasData = await WaitToReadAsync(cancellationToken).ConfigureAwait(false);
                if (!hasData)
                {
                    throw new ChannelClosedException();
                }

                if (TryRead(out var item))
                {
                    return item;
                }
            }
        }

        /// <inheritdoc />
        public override ValueTask<bool> WaitToReadAsync(CancellationToken cancellationToken = default)
        {
            if (HasBufferedItems)
            {
                return new ValueTask<bool>(true);
            }

            if (TryStageFromAllPriorities())
            {
                return new ValueTask<bool>(true);
            }

            return _singleLane
                ? WaitToReadSingleLaneAsync(cancellationToken)
                : WaitToReadSlowAsync(cancellationToken);
        }

        private async ValueTask<bool> WaitToReadSlowAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_laneCases.Length == 0)
            {
                return false;
            }

            Result<Go.Unit> selectResult = await Go.SelectAsync(provider: null, cancellationToken, _laneCases).ConfigureAwait(false);
            if (selectResult.IsSuccess)
            {
                return HasBufferedItems;
            }

            Error error = selectResult.Error ?? Error.Unspecified();
            if (string.Equals(error.Code, ErrorCodes.SelectDrained, StringComparison.Ordinal))
            {
                return HasBufferedItems;
            }

            if (string.Equals(error.Code, ErrorCodes.Canceled, StringComparison.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw error.Cause ?? new OperationCanceledException(error.Message, cancellationToken);
            }

            throw error.Cause ?? new InvalidOperationException(error.Message);
        }

        private async ValueTask<bool> WaitToReadSingleLaneAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!await _readers[0].WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            TryStageFromPriority(0);
            return HasBufferedItems;
        }

        private ChannelCase<Go.Unit>[] BuildLaneCases(ChannelReader<T>[] readers)
        {
            var cases = new ChannelCase<Go.Unit>[readers.Length];
            for (var priority = 0; priority < readers.Length; priority++)
            {
                var laneIndex = priority;
                cases[priority] = ChannelCase<Go.Unit>
                    .Create(readers[priority], (value, ct) => StageLaneValueAsync(laneIndex, value, ct))
                    .WithPriority(laneIndex);
            }

            return cases;
        }

        private ValueTask<Result<Go.Unit>> StageLaneValueAsync(int priority, T value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BufferItem(priority, value);
            TryStageFromPriority(priority);

            return ValueTask.FromResult(Result.Ok(Go.Unit.Value));
        }

        private void BufferItem(int priority, T item)
        {
            _buffers[priority].Enqueue(item);
            Interlocked.Increment(ref _bufferedPerPriority[priority]);
            Interlocked.Increment(ref _bufferedTotal);
        }

        private bool HasBufferedItems => Volatile.Read(ref _bufferedTotal) > 0;

        private bool TryReadFromBuffers(out T item)
        {
            if (_singleLane)
            {
                if (_buffers[0].TryDequeue(out item!))
                {
                    Interlocked.Decrement(ref _bufferedPerPriority[0]);
                    Interlocked.Decrement(ref _bufferedTotal);
                    return true;
                }

                item = default!;
                return false;
            }

            for (var priority = 0; priority < _buffers.Length; priority++)
            {
                if (_buffers[priority].TryDequeue(out item!))
                {
                    Interlocked.Decrement(ref _bufferedPerPriority[priority]);
                    Interlocked.Decrement(ref _bufferedTotal);
                    return true;
                }
            }

            item = default!;
            return false;
        }

        private bool TryStageFromAllPriorities()
        {
            if (_singleLane)
            {
                return TryStageFromPriority(0);
            }

            var staged = false;
            for (var priority = 0; priority < _readers.Length; priority++)
            {
                staged |= TryStageFromPriority(priority);
            }

            return staged;
        }

        private bool TryStageFromPriority(int priority)
        {
            var staged = false;
            var buffered = Volatile.Read(ref _bufferedPerPriority[priority]);
            while (buffered < _prefetchPerPriority && _readers[priority].TryRead(out var item))
            {
                BufferItem(priority, item);
                buffered++;
                staged = true;
            }

            return staged;
        }
    }

    /// <summary>
    /// Writer implementation that routes items to the appropriate priority lane.
    /// </summary>
    public sealed class PrioritizedChannelWriter : ChannelWriter<T>
    {
        private readonly ChannelWriter<T>[] _writers;
        private readonly int _defaultPriority;

        internal PrioritizedChannelWriter(ChannelWriter<T>[] writers, int defaultPriority)
        {
            _writers = writers ?? throw new ArgumentNullException(nameof(writers));
            if (defaultPriority < 0 || defaultPriority >= writers.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(defaultPriority));
            }

            _defaultPriority = defaultPriority;
        }

        /// <summary>Asynchronously writes an item to the specified priority lane.</summary>
        /// <param name="item">The item to write.</param>
        /// <param name="priority">The zero-based priority lane; lower values represent higher priority.</param>
        /// <param name="cancellationToken">The token used to cancel the write.</param>
        /// <returns>A task that completes when the item has been written.</returns>
        public ValueTask WriteAsync(T item, int priority, CancellationToken cancellationToken = default) =>
            GetWriter(priority).WriteAsync(item, cancellationToken);

        /// <summary>Attempts to write an item to the specified priority lane.</summary>
        /// <param name="item">The item to write.</param>
        /// <param name="priority">The zero-based priority lane; lower values represent higher priority.</param>
        /// <returns><see langword="true"/> when the item was written; otherwise <see langword="false"/>.</returns>
        public bool TryWrite(T item, int priority) => GetWriter(priority).TryWrite(item);

        /// <summary>Waits until space is available in the specified priority lane.</summary>
        /// <param name="priority">The zero-based priority lane; lower values represent higher priority.</param>
        /// <param name="cancellationToken">The token used to cancel the wait.</param>
        /// <returns><see langword="true"/> when space became available; otherwise <see langword="false"/> if the writer completed.</returns>
        public ValueTask<bool> WaitToWriteAsync(int priority, CancellationToken cancellationToken = default) =>
            GetWriter(priority).WaitToWriteAsync(cancellationToken);

        /// <inheritdoc />
        public override ValueTask WriteAsync(T item, CancellationToken cancellationToken = default) =>
            WriteAsync(item, _defaultPriority, cancellationToken);

        /// <inheritdoc />
        public override bool TryWrite(T item) => TryWrite(item, _defaultPriority);

        /// <inheritdoc />
        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) =>
            WaitToWriteAsync(_defaultPriority, cancellationToken);

        /// <inheritdoc />
        public override bool TryComplete(Exception? error = null)
        {
            var success = true;
            foreach (var writer in _writers)
            {
                success &= writer.TryComplete(error);
            }

            return success;
        }

        private ChannelWriter<T> GetWriter(int priority) => (uint)priority >= (uint)_writers.Length ? throw new ArgumentOutOfRangeException(nameof(priority)) : _writers[priority];
    }
}
