using BenchmarkDotNet.Attributes;

using Hugo;

namespace Hugo.Benchmarks;

[MemoryDiagnoser]
[BenchmarkCategory(BenchmarkCategories.Results)]
public class ResultMetadataBenchmarks
{
    [Params(32, 128)]
    public int OperationCount { get; set; }

    [Benchmark(Baseline = true)]
    public Result<int> PlainPipeline()
    {
        var result = Go.Ok(0);
        for (var i = 0; i < OperationCount; i++)
        {
            result = result.Map(static value => value + 1);
        }

        return result;
    }

    [Benchmark]
    public Result<int> MetadataOnErrors()
    {
        var result = Go.Ok(0);
        for (var i = 0; i < OperationCount; i++)
        {
            result = result
                .Ensure(static value => value % 5 != 0, static value => Error.From("divisible by five", "error.result"))
                .Recover(static error =>
                {
                    var enriched = error
                        .WithMetadata("code", error.Code)
                        .WithMetadata("messageLength", error.Message.Length)
                        .WithMetadata("timestamp", DateTimeOffset.UtcNow);

                    return Result.Ok(enriched.Message.Length);
                });
        }

        return result;
    }

}
