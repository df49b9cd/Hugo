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

    /// <summary>
    /// Gets or sets the number of priority levels supported by the channel. Lower indices represent higher priority.
    /// </summary>
    public int PriorityLevels
    {
        get => _priorityLevels;
        set
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

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

    /// <summary>
    /// Initializes a new instance of the <see cref="PrioritizedChannel{T}"/> class.
    /// </summary>
    public PrioritizedChannel(PrioritizedChannelOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var levels = options.PriorityLevels;
        if (options.DefaultPriority is { } defaultPriority && defaultPriority >= levels)
        {
            throw new ArgumentOutOfRangeException(nameof(options.DefaultPriority), "Default priority must be less than the number of priority levels.");
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
        _reader = new PrioritizedChannelReader(readers);
        _writer = new PrioritizedChannelWriter(writers, resolvedDefaultPriority);
        PriorityLevels = levels;
        DefaultPriority = resolvedDefaultPriority;
        CapacityPerLevel = capacity;
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
        private readonly ConcurrentQueue<T> _buffer = new();
        private readonly Task _completion;

        internal PrioritizedChannelReader(ChannelReader<T>[] readers)
        {
            _readers = readers ?? throw new ArgumentNullException(nameof(readers));
            var completions = new Task[readers.Length];
            for (var i = 0; i < readers.Length; i++)
            {
                completions[i] = readers[i].Completion;
            }
            _completion = Task.WhenAll(completions);
        }

        /// <inheritdoc />
        public override Task Completion => _completion;

        /// <inheritdoc />
        public override bool TryRead(out T item)
        {
            if (_buffer.TryDequeue(out item!))
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
            if (!_buffer.IsEmpty)
            {
                return new ValueTask<bool>(true);
            }

            for (var i = 0; i < _readers.Length; i++)
            {
                if (_readers[i].TryRead(out var item))
                {
                    _buffer.Enqueue(item);
                    return new ValueTask<bool>(true);
                }
            }

            return WaitToReadSlowAsync(cancellationToken);
        }

        private async ValueTask<bool> WaitToReadSlowAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var waitTasks = new Task<bool>[_readers.Length];
                for (var i = 0; i < _readers.Length; i++)
                {
                    waitTasks[i] = _readers[i].WaitToReadAsync(cancellationToken).AsTask();
                }

                var completed = await Task.WhenAny(waitTasks).ConfigureAwait(false);

                if (completed.IsFaulted)
                {
                    await Task.FromException(completed.Exception!).ConfigureAwait(false);
                }

                var hasItems = false;
                for (var priority = 0; priority < _readers.Length; priority++)
                {
                    var task = waitTasks[priority];
                    if (task.IsCompletedSuccessfully && task.Result)
                    {
                        while (_readers[priority].TryRead(out var item))
                        {
                            _buffer.Enqueue(item);
                            hasItems = true;
                        }
                    }
                }

                if (hasItems)
                {
                    return true;
                }

                var allCompleted = true;
                for (var i = 0; i < waitTasks.Length; i++)
                {
                    var task = waitTasks[i];
                    if (!task.IsCompleted)
                    {
                        allCompleted = false;
                        break;
                    }

                    if (task.IsCompletedSuccessfully && task.Result)
                    {
                        allCompleted = false;
                        break;
                    }
                }

                if (allCompleted && _buffer.IsEmpty)
                {
                    return false;
                }
            }
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

        /// <summary>
        /// Asynchronously writes an item to the specified priority lane.
        /// </summary>
        public ValueTask WriteAsync(T item, int priority, CancellationToken cancellationToken = default) =>
            GetWriter(priority).WriteAsync(item, cancellationToken);

        /// <summary>
        /// Attempts to write an item to the specified priority lane.
        /// </summary>
        public bool TryWrite(T item, int priority) => GetWriter(priority).TryWrite(item);

        /// <summary>
        /// Waits until space is available in the specified priority lane.
        /// </summary>
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
