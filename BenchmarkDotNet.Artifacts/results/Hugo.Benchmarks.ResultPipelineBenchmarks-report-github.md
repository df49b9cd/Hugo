```

BenchmarkDotNet v0.15.4, macOS 26.0.1 (25A362) [Darwin 25.0.0]
Apple M1 Max 2.40GHz, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100-rc.2.25502.107
  [Host]     : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2
  DefaultJob : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2


```
| Method                  | OperationCount | Mean      | Error    | StdDev   | Gen0    | Allocated |
|------------------------ |--------------- |----------:|---------:|---------:|--------:|----------:|
| **ResultPipeline**          | **32**             | **146.24 μs** | **0.389 μs** | **0.345 μs** |  **0.2441** |    **1920 B** |
| ResultPipelineAsync     | 32             |  54.55 μs | 1.066 μs | 1.269 μs |  3.6621 |   23453 B |
| ExceptionPipeline       | 32             | 222.50 μs | 0.748 μs | 0.700 μs |  0.2441 |    2448 B |
| PatternMatchingPipeline | 32             | 139.20 μs | 0.701 μs | 0.656 μs |       - |         - |
| **ResultPipeline**          | **128**            | **575.31 μs** | **1.018 μs** | **0.850 μs** |       **-** |    **8064 B** |
| ResultPipelineAsync     | 128            | 187.90 μs | 3.692 μs | 6.751 μs | 13.6719 |   95389 B |
| ExceptionPipeline       | 128            | 887.33 μs | 4.610 μs | 4.312 μs |  0.9766 |    8976 B |
| PatternMatchingPipeline | 128            | 565.24 μs | 3.291 μs | 2.748 μs |       - |         - |
