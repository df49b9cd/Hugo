```

BenchmarkDotNet v0.15.4, macOS 26.0.1 (25A362) [Darwin 25.0.0]
Apple M1 Max 2.40GHz, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100-rc.2.25502.107
  [Host]     : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2
  DefaultJob : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2


```
| Method                     | OperationCount | Mean      | Error     | StdDev    | Gen0    | Gen1   | Gen2   | Allocated |
|--------------------------- |--------------- |----------:|----------:|----------:|--------:|-------:|-------:|----------:|
| **HugoPoolRoundTrip**          | **128**            |  **2.699 μs** | **0.0034 μs** | **0.0032 μs** |  **0.1640** |      **-** |      **-** |   **1.02 KB** |
| DefaultObjectPoolRoundTrip | 128            |  1.904 μs | 0.0033 μs | 0.0031 μs |  0.1640 |      - |      - |   1.02 KB |
| ConcurrentBagRoundTrip     | 128            | 10.751 μs | 0.2143 μs | 0.2104 μs |  1.3123 | 0.6561 | 0.0153 |    8.1 KB |
| **HugoPoolRoundTrip**          | **1024**           | **19.054 μs** | **0.0187 μs** | **0.0156 μs** |  **1.2817** |      **-** |      **-** |   **8.02 KB** |
| DefaultObjectPoolRoundTrip | 1024           | 15.267 μs | 0.0491 μs | 0.0410 μs |  1.2817 |      - |      - |   8.02 KB |
| ConcurrentBagRoundTrip     | 1024           | 61.709 μs | 0.3727 μs | 0.3486 μs | 10.3760 | 5.1270 | 0.9766 |  64.17 KB |
