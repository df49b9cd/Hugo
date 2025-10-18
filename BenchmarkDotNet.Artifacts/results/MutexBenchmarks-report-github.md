```

BenchmarkDotNet v0.15.4, macOS 26.0.1 (25A362) [Darwin 25.0.0]
Apple M1 Max 2.40GHz, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100-rc.2.25502.107
  [Host]     : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2
  DefaultJob : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2


```
| Method             | Mean     | Error    | StdDev   | Ratio | RatioSD | Gen0    | Allocated | Alloc Ratio |
|------------------- |---------:|---------:|---------:|------:|--------:|--------:|----------:|------------:|
| HugoMutexAsync     | 593.4 μs | 10.90 μs | 10.20 μs |  1.20 |    0.02 | 27.3438 | 185.73 KB |        1.57 |
| SemaphoreSlimAsync | 493.9 μs |  4.80 μs |  4.49 μs |  1.00 |    0.01 | 19.5313 | 118.31 KB |        1.00 |
