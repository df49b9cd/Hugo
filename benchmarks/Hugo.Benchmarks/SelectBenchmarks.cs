using System.Threading.Channels;

using BenchmarkDotNet.Attributes;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
public class SelectBenchmarks
{
    [Params(3, 6)]
    public int ChannelCount { get; set; }

    [Params(512, 2048)]
    public int MessageCount { get; set; }

    [Params(false, true)]
    public bool EnableTimeouts { get; set; }

    private Channel<int>[] CreateChannels()
    {
        var channels = new Channel<int>[ChannelCount];
        for (var i = 0; i < ChannelCount; i++)
        {
            channels[i] = Channel.CreateBounded<int>(new BoundedChannelOptions(128)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false
            });
        }

        return channels;
    }

    [Benchmark]
    public async Task GoSelectAsync()
    {
        var channels = CreateChannels();
        var producers = StartProducers(channels);
        var remaining = MessageCount;
        var total = 0L;
        var timeout = EnableTimeouts ? TimeSpan.FromMilliseconds(1) : Timeout.InfiniteTimeSpan;

        while (remaining > 0)
        {
            var cases = new ChannelCase[channels.Length];
            for (var i = 0; i < channels.Length; i++)
            {
                var reader = channels[i].Reader;
                cases[i] = ChannelCase.Create(reader, async (value, ct) =>
                {
                    total += value;
                    BenchmarkWorkloads.SimulateLightCpuWork();
                    await Task.Yield();
                    Interlocked.Decrement(ref remaining);
                    return Result.Ok(Go.Unit.Value);
                });
            }

            if (EnableTimeouts)
            {
                var result = await Go.SelectAsync(timeout, null, CancellationToken.None, cases).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    BenchmarkWorkloads.SimulateLightCpuWork();
                }
            }
            else
            {
                var result = await Go.SelectAsync(null, CancellationToken.None, cases).ConfigureAwait(false);
                if (result.IsFailure)
                {
                    break;
                }
            }
        }

        await Task.WhenAll(producers).ConfigureAwait(false);
        GC.KeepAlive(total);
    }

    [Benchmark]
    public async Task TaskWhenAnyAsync()
    {
        var channels = CreateChannels();
        var producers = StartProducers(channels);
        var readers = channels.Select(c => c.Reader).ToArray();
        var tasks = readers.Select(r => r.ReadAsync().AsTask()).ToList();
        var remaining = MessageCount;
        var total = 0L;
        var timeoutTask = EnableTimeouts ? Task.Delay(TimeSpan.FromMilliseconds(1)) : null;

        while (remaining > 0 && tasks.Count > 0)
        {
            Task completedTask;
            if (timeoutTask is null)
            {
                completedTask = await Task.WhenAny(tasks).ConfigureAwait(false);
            }
            else
            {
                var combined = tasks.Append(timeoutTask).ToArray();
                completedTask = await Task.WhenAny(combined).ConfigureAwait(false);
                if (completedTask == timeoutTask)
                {
                    BenchmarkWorkloads.SimulateLightCpuWork();
                    timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(1));
                    continue;
                }
            }

            var index = tasks.IndexOf((Task<int>)completedTask);
            if (index < 0)
            {
                break;
            }

            try
            {
                var value = await ((Task<int>)completedTask).ConfigureAwait(false);
                total += value;
                BenchmarkWorkloads.SimulateLightCpuWork();
                remaining--;
                tasks[index] = readers[index].ReadAsync().AsTask();
            }
            catch (ChannelClosedException)
            {
                tasks.RemoveAt(index);
                readers = [.. readers.Where((_, i) => i != index)];
            }
        }

        await Task.WhenAll(producers).ConfigureAwait(false);
        GC.KeepAlive(total);
    }

    [Benchmark]
    public async Task PollingAsync()
    {
        var channels = CreateChannels();
        var producers = StartProducers(channels);
        var remaining = MessageCount;
        var total = 0L;

        while (remaining > 0)
        {
            var progress = false;
            for (var i = 0; i < channels.Length; i++)
            {
                if (channels[i].Reader.TryRead(out var value))
                {
                    total += value;
                    BenchmarkWorkloads.SimulateLightCpuWork();
                    remaining--;
                    progress = true;
                    if (remaining == 0)
                    {
                        break;
                    }
                }
            }

            if (!progress)
            {
                if (EnableTimeouts)
                {
                    await Task.Delay(1).ConfigureAwait(false);
                }
                else
                {
                    await Task.Yield();
                }
            }
        }

        await Task.WhenAll(producers).ConfigureAwait(false);
        GC.KeepAlive(total);
    }

    private Task[] StartProducers(Channel<int>[] channels)
    {
        var perChannel = Math.Max(1, MessageCount / channels.Length);
        var remainder = MessageCount % channels.Length;
        var tasks = new Task[channels.Length];

        for (var i = 0; i < channels.Length; i++)
        {
            var writer = channels[i].Writer;
            var quota = perChannel + (i < remainder ? 1 : 0);
            var seed = i;

            tasks[i] = Task.Run(async () =>
            {
                for (var j = 0; j < quota; j++)
                {
                    await writer.WriteAsync(seed + j).ConfigureAwait(false);
                    BenchmarkWorkloads.SimulateLightCpuWork();
                }

                writer.TryComplete();
            });
        }

        return tasks;
    }
}
