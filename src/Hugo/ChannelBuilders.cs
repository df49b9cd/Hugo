using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;

namespace Hugo;

/// <summary>Provides a fluent API for configuring bounded channels before creation.</summary>
public sealed class BoundedChannelBuilder<T>
{
    private int _capacity;
    private BoundedChannelFullMode _fullMode = BoundedChannelFullMode.Wait;
    private bool _singleReader;
    private bool _singleWriter;
    private bool _allowSynchronousContinuations;
    private Action<BoundedChannelOptions>? _configure;

    internal BoundedChannelBuilder(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
    }

    /// <summary>Sets the buffer capacity for the channel.</summary>
    /// <param name="capacity">The maximum number of items the channel may buffer.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public BoundedChannelBuilder<T> WithCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
        return this;
    }

    /// <summary>Configures how the channel behaves when it reaches capacity.</summary>
    /// <param name="fullMode">The backpressure mode applied when the channel is full.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public BoundedChannelBuilder<T> WithFullMode(BoundedChannelFullMode fullMode)
    {
        _fullMode = fullMode;
        return this;
    }

    /// <summary>Flags whether the channel is optimized for a single reader.</summary>
    /// <param name="enabled"><see langword="true"/> to optimize for a single reader; otherwise <see langword="false"/>.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public BoundedChannelBuilder<T> SingleReader(bool enabled = true)
    {
        _singleReader = enabled;
        return this;
    }

    /// <summary>Flags whether the channel is optimized for a single writer.</summary>
    /// <param name="enabled"><see langword="true"/> to optimize for a single writer; otherwise <see langword="false"/>.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public BoundedChannelBuilder<T> SingleWriter(bool enabled = true)
    {
        _singleWriter = enabled;
        return this;
    }

    /// <summary>Enables synchronous continuations on channel operations.</summary>
    /// <param name="enabled"><see langword="true"/> to allow synchronous continuations; otherwise <see langword="false"/>.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public BoundedChannelBuilder<T> AllowSynchronousContinuations(bool enabled = true)
    {
        _allowSynchronousContinuations = enabled;
        return this;
    }

    /// <summary>Adds a custom configuration delegate that runs against the channel options.</summary>
    /// <param name="configure">The callback used to tweak <see cref="BoundedChannelOptions"/>.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public BoundedChannelBuilder<T> Configure(Action<BoundedChannelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _configure += configure;
        return this;
    }

    internal BoundedChannelOptions CreateOptions()
    {
        var options = new BoundedChannelOptions(_capacity)
        {
            FullMode = _fullMode,
            SingleReader = _singleReader,
            SingleWriter = _singleWriter,
            AllowSynchronousContinuations = _allowSynchronousContinuations
        };

        _configure?.Invoke(options);

        return options;
    }

    /// <summary>Creates a <see cref="Channel{T}"/> using the accumulated configuration.</summary>
    /// <returns>The configured bounded channel.</returns>
    public Channel<T> Build() => Channel.CreateBounded<T>(CreateOptions());
}

/// <summary>Provides configuration helpers for prioritized channels.</summary>
public sealed class PrioritizedChannelBuilder<T>
{
    private int _priorityLevels;
    private int? _defaultPriority;
    private int? _capacityPerLevel;
    private BoundedChannelFullMode _fullMode = BoundedChannelFullMode.Wait;
    private bool _singleReader;
    private bool _singleWriter;
    private Action<PrioritizedChannelOptions>? _configure;

    internal PrioritizedChannelBuilder()
        : this(3)
    {
    }

    internal PrioritizedChannelBuilder(int priorityLevels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(priorityLevels);

        _priorityLevels = priorityLevels;
    }

    /// <summary>Sets the number of available priority levels in the channel.</summary>
    /// <param name="priorityLevels">The number of priority queues to maintain.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public PrioritizedChannelBuilder<T> WithPriorityLevels(int priorityLevels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(priorityLevels);

        if (_defaultPriority.HasValue && _defaultPriority.Value >= priorityLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(priorityLevels), "Priority levels must exceed the configured default priority.");
        }

        _priorityLevels = priorityLevels;
        return this;
    }

    /// <summary>Defines the priority used when enqueuing without an explicit priority.</summary>
    /// <param name="priority">The default priority index to use.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public PrioritizedChannelBuilder<T> WithDefaultPriority(int priority)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(priority);

        if (priority >= _priorityLevels)
        {
            throw new ArgumentOutOfRangeException(nameof(priority), "Default priority must be less than the number of priority levels.");
        }

        _defaultPriority = priority;
        return this;
    }

    /// <summary>Clears the previously configured default priority.</summary>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public PrioritizedChannelBuilder<T> WithoutDefaultPriority()
    {
        _defaultPriority = null;
        return this;
    }

    /// <summary>Sets the per-priority capacity limit.</summary>
    /// <param name="capacity">The maximum buffered items allowed at each priority level.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public PrioritizedChannelBuilder<T> WithCapacityPerLevel(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacityPerLevel = capacity;
        return this;
    }

    /// <summary>Removes any per-priority capacity constraints.</summary>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public PrioritizedChannelBuilder<T> WithoutCapacityLimits()
    {
        _capacityPerLevel = null;
        return this;
    }

    /// <summary>Configures how the channel handles writers when the target priority queue is full.</summary>
    /// <param name="fullMode">The backpressure mode applied to the prioritized channel.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public PrioritizedChannelBuilder<T> WithFullMode(BoundedChannelFullMode fullMode)
    {
        _fullMode = fullMode;
        return this;
    }

    /// <summary>Flags whether the channel is optimized for a single reader.</summary>
    /// <param name="enabled"><see langword="true"/> to optimize for a single reader; otherwise <see langword="false"/>.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public PrioritizedChannelBuilder<T> SingleReader(bool enabled = true)
    {
        _singleReader = enabled;
        return this;
    }

    /// <summary>Flags whether the channel is optimized for a single writer.</summary>
    /// <param name="enabled"><see langword="true"/> to optimize for a single writer; otherwise <see langword="false"/>.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public PrioritizedChannelBuilder<T> SingleWriter(bool enabled = true)
    {
        _singleWriter = enabled;
        return this;
    }

    /// <summary>Adds a custom configuration delegate that runs against the channel options.</summary>
    /// <param name="configure">The callback used to tweak <see cref="PrioritizedChannelOptions"/>.</param>
    /// <returns>The current builder for chaining additional configuration.</returns>
    public PrioritizedChannelBuilder<T> Configure(Action<PrioritizedChannelOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        _configure += configure;
        return this;
    }

    internal PrioritizedChannelOptions CreateOptions()
    {
        var options = new PrioritizedChannelOptions
        {
            PriorityLevels = _priorityLevels,
            CapacityPerLevel = _capacityPerLevel,
            FullMode = _fullMode,
            SingleReader = _singleReader,
            SingleWriter = _singleWriter
        };

        if (_defaultPriority.HasValue)
        {
            options.DefaultPriority = _defaultPriority.Value;
        }

        _configure?.Invoke(options);

        return options;
    }

    /// <summary>Creates a <see cref="PrioritizedChannel{T}"/> using the accumulated configuration.</summary>
    /// <returns>The configured prioritized channel.</returns>
    public PrioritizedChannel<T> Build() => new(CreateOptions());
}

/// <summary>Registers channel abstractions with the dependency injection container.</summary>
public static class ChannelServiceCollectionExtensions
{
    /// <summary>Adds a bounded <see cref="Channel{T}"/> and its reader and writer to the service collection.</summary>
    /// <typeparam name="T">The payload type flowing through the channel.</typeparam>
    /// <param name="services">The service collection to modify.</param>
    /// <param name="capacity">The maximum number of buffered items.</param>
    /// <param name="configure">An optional callback to further configure the channel builder.</param>
    /// <param name="lifetime">The lifetime used for the registered services.</param>
    /// <returns>The service collection to support fluent chaining.</returns>
    public static IServiceCollection AddBoundedChannel<T>(this IServiceCollection services, int capacity, Action<BoundedChannelBuilder<T>>? configure = null, ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new BoundedChannelBuilder<T>(capacity);
        configure?.Invoke(builder);

        return RegisterBoundedChannel(services, builder, lifetime);
    }

    /// <summary>Adds a prioritized channel and its associated readers and writers to the service collection.</summary>
    /// <typeparam name="T">The payload type flowing through the channel.</typeparam>
    /// <param name="services">The service collection to modify.</param>
    /// <param name="priorityLevels">The number of priority queues in the channel.</param>
    /// <param name="configure">An optional callback to further configure the channel builder.</param>
    /// <param name="lifetime">The lifetime used for the registered services.</param>
    /// <returns>The service collection to support fluent chaining.</returns>
    public static IServiceCollection AddPrioritizedChannel<T>(this IServiceCollection services, int priorityLevels = 3, Action<PrioritizedChannelBuilder<T>>? configure = null, ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new PrioritizedChannelBuilder<T>(priorityLevels);
        configure?.Invoke(builder);

        return RegisterPrioritizedChannel(services, builder, lifetime);
    }

    private static IServiceCollection RegisterBoundedChannel<T>(IServiceCollection services, BoundedChannelBuilder<T> builder, ServiceLifetime lifetime)
    {
        services.Add(new ServiceDescriptor(typeof(Channel<T>), _ => builder.Build(), lifetime));
        services.Add(new ServiceDescriptor(typeof(ChannelReader<T>), sp => sp.GetRequiredService<Channel<T>>().Reader, lifetime));
        services.Add(new ServiceDescriptor(typeof(ChannelWriter<T>), sp => sp.GetRequiredService<Channel<T>>().Writer, lifetime));
        return services;
    }

    private static IServiceCollection RegisterPrioritizedChannel<T>(IServiceCollection services, PrioritizedChannelBuilder<T> builder, ServiceLifetime lifetime)
    {
        services.Add(new ServiceDescriptor(typeof(PrioritizedChannel<T>), _ => builder.Build(), lifetime));
        services.Add(new ServiceDescriptor(typeof(ChannelReader<T>), sp => sp.GetRequiredService<PrioritizedChannel<T>>().Reader, lifetime));
        services.Add(new ServiceDescriptor(typeof(ChannelWriter<T>), sp => sp.GetRequiredService<PrioritizedChannel<T>>().Writer, lifetime));
        services.Add(new ServiceDescriptor(typeof(PrioritizedChannel<T>.PrioritizedChannelReader), sp => sp.GetRequiredService<PrioritizedChannel<T>>().PrioritizedReader, lifetime));
        services.Add(new ServiceDescriptor(typeof(PrioritizedChannel<T>.PrioritizedChannelWriter), sp => sp.GetRequiredService<PrioritizedChannel<T>>().PrioritizedWriter, lifetime));
        return services;
    }
}
