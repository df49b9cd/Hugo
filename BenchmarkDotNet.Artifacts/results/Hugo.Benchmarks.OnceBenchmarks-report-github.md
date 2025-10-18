```

BenchmarkDotNet v0.15.4, macOS 26.0.1 (25A362) [Darwin 25.0.0]
Apple M1 Max 2.40GHz, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100-rc.2.25502.107
  [Host]     : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2
  Job-CNUJVU : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2

InvocationCount=1  UnrollFactor=1  

```
| Method                 | TaskCount | Mean      | Error     | StdDev   | Allocated |
|----------------------- |---------- |----------:|----------:|---------:|----------:|
| **HugoOnceAsync**          | **16**        | **107.53 μs** | **15.486 μs** | **42.65 μs** |   **1.64 KB** |
| LazyValueAsync         | 16        |  94.86 μs |  8.581 μs | 23.92 μs |   1.59 KB |
| LazyInitializerAsync   | 16        |  93.29 μs |  9.817 μs | 27.69 μs |   2.55 KB |
| DoubleCheckedLockAsync | 16        |  93.47 μs |  9.872 μs | 26.86 μs |   1.53 KB |
| **HugoOnceAsync**          | **64**        | **113.68 μs** | **11.259 μs** | **31.57 μs** |   **5.02 KB** |
| LazyValueAsync         | 64        | 107.83 μs | 10.799 μs | 30.28 μs |   5.03 KB |
| LazyInitializerAsync   | 64        | 113.22 μs | 11.323 μs | 32.30 μs |   8.93 KB |
| DoubleCheckedLockAsync | 64        | 116.60 μs | 10.376 μs | 30.43 μs |   4.87 KB |
