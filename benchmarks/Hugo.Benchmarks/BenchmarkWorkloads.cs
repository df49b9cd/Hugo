using System.Runtime.CompilerServices;

namespace Hugo.Benchmarks;

internal static class BenchmarkWorkloads
{
    private const int SpinIterations = 256;
    private const int LightSpinIterations = 64;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SimulateCpuWork() => Thread.SpinWait(SpinIterations);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SimulateLightCpuWork() => Thread.SpinWait(LightSpinIterations);

    public static async Task SimulateAsyncWorkAsync(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 2; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Thread.SpinWait(SpinIterations);
            await Task.Yield();
        }
    }

    public static CancellationTokenSource CreateCancellationScope(bool enableCancellation, int cancelAfterMilliseconds = 2)
    {
        if (!enableCancellation)
            return new CancellationTokenSource();

        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(cancelAfterMilliseconds));
        return cts;
    }
}
