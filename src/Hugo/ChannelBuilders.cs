using System.Threading.Channels;

using Microsoft.Extensions.DependencyInjection;

namespace Hugo;

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

    public BoundedChannelBuilder<T> WithCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacity = capacity;
        return this;
    }

    public BoundedChannelBuilder<T> WithFullMode(BoundedChannelFullMode fullMode)
    {
        _fullMode = fullMode;
        return this;
    }

    public BoundedChannelBuilder<T> SingleReader(bool enabled = true)
    {
        _singleReader = enabled;
        return this;
    }

    public BoundedChannelBuilder<T> SingleWriter(bool enabled = true)
    {
        _singleWriter = enabled;
        return this;
    }

    public BoundedChannelBuilder<T> AllowSynchronousContinuations(bool enabled = true)
    {
        _allowSynchronousContinuations = enabled;
        return this;
    }

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

    public Channel<T> Build() => Channel.CreateBounded<T>(CreateOptions());
}

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

    public PrioritizedChannelBuilder<T> WithoutDefaultPriority()
    {
        _defaultPriority = null;
        return this;
    }

    public PrioritizedChannelBuilder<T> WithCapacityPerLevel(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        _capacityPerLevel = capacity;
        return this;
    }

    public PrioritizedChannelBuilder<T> WithoutCapacityLimits()
    {
        _capacityPerLevel = null;
        return this;
    }

    public PrioritizedChannelBuilder<T> WithFullMode(BoundedChannelFullMode fullMode)
    {
        _fullMode = fullMode;
        return this;
    }

    public PrioritizedChannelBuilder<T> SingleReader(bool enabled = true)
    {
        _singleReader = enabled;
        return this;
    }

    public PrioritizedChannelBuilder<T> SingleWriter(bool enabled = true)
    {
        _singleWriter = enabled;
        return this;
    }

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

    public PrioritizedChannel<T> Build() => new(CreateOptions());
}

public static class ChannelServiceCollectionExtensions
{
    public static IServiceCollection AddBoundedChannel<T>(this IServiceCollection services, int capacity, Action<BoundedChannelBuilder<T>>? configure = null, ServiceLifetime lifetime = ServiceLifetime.Singleton)
    {
        ArgumentNullException.ThrowIfNull(services);

        var builder = new BoundedChannelBuilder<T>(capacity);
        configure?.Invoke(builder);

        return RegisterBoundedChannel(services, builder, lifetime);
    }

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
