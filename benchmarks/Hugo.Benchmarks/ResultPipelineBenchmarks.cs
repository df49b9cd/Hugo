using BenchmarkDotNet.Attributes;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
public class ResultPipelineBenchmarks
{
    [Params(32, 128)]
    public int OperationCount { get; set; }

    [Benchmark]
    public Result<int> ResultPipeline()
    {
        var result = Go.Ok(0);
        for (var i = 0; i < OperationCount; i++)
        {
            result = result
                .Map(static value => value + 1)
                .Ensure(static value => value % 3 != 0, static value => Error.From($"Value {value} divisible by three."))
                .Recover(static error => Result.Ok(error.Message.Length))
                .Tap(static _ => BenchmarkWorkloads.SimulateLightCpuWork());
        }

        return result;
    }

    [Benchmark]
    public async Task<Result<int>> ResultPipelineAsync()
    {
        var result = Go.Ok(0);
        for (var i = 0; i < OperationCount; i++)
        {
            result = await result
                .MapAsync(static (value, _) => Task.FromResult(value + 1))
                .EnsureAsync(static async (value, _) =>
                {
                    await Task.Yield();
                    return value % 5 != 0;
                }, errorFactory: static value => Error.From($"Value {value} divisible by five."))
                .RecoverAsync(static (error, _) => Task.FromResult(Result.Ok(error.Message.Length)))
                .ConfigureAwait(false);
        }

        return result;
    }

    [Benchmark]
    public int ExceptionPipeline()
    {
        var value = 0;
        for (var i = 0; i < OperationCount; i++)
        {
            try
            {
                value = ExceptionStep(value);
            }
            catch (InvalidOperationException ex)
            {
                BenchmarkWorkloads.SimulateLightCpuWork();
                value = ex.Message.Length;
            }
        }

        return value;
    }

    [Benchmark]
    public int PatternMatchingPipeline()
    {
        var state = new PipelineState(0, PipelineStatus.Success);
        for (var i = 0; i < OperationCount; i++)
        {
            state = state.Status switch
            {
                PipelineStatus.Success => StepSuccess(state),
                PipelineStatus.Warning => StepWarning(state),
                _ => StepFailure(state)
            };
        }

        return state.Value;
    }

    private static int ExceptionStep(int value)
    {
        BenchmarkWorkloads.SimulateLightCpuWork();
        if (value % 4 == 0)
        {
            throw new InvalidOperationException($"Value {value} rejected");
        }

        return value + 1;
    }

    private static PipelineState StepSuccess(PipelineState state)
    {
        BenchmarkWorkloads.SimulateLightCpuWork();
        var next = state.Value + 1;
        return next % 7 == 0
            ? new PipelineState(next, PipelineStatus.Warning)
            : new PipelineState(next, PipelineStatus.Success);
    }

    private static PipelineState StepWarning(PipelineState state)
    {
        BenchmarkWorkloads.SimulateLightCpuWork();
        var next = state.Value + 2;
        return next % 5 == 0
            ? new PipelineState(next, PipelineStatus.Failure)
            : new PipelineState(next, PipelineStatus.Success);
    }

    private static PipelineState StepFailure(PipelineState state)
    {
        BenchmarkWorkloads.SimulateLightCpuWork();
        return new PipelineState(0, PipelineStatus.Success);
    }

    private readonly record struct PipelineState(int Value, PipelineStatus Status);

    private enum PipelineStatus
    {
        Success,
        Warning,
        Failure
    }
}
