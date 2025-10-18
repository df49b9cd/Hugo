```

BenchmarkDotNet v0.15.4, macOS 26.0.1 (25A362) [Darwin 25.0.0]
Apple M1 Max 2.40GHz, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100-rc.2.25502.107
  [Host]     : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2
  DefaultJob : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2


```
| Method                 | SingleReader | SingleWriter | Mean      | Error    | StdDev    | Gen0   | Allocated |
|----------------------- |------------- |------------- |----------:|---------:|----------:|-------:|----------:|
| **GoMakeUnboundedAsync**   | **False**        | **False**        | **107.18 μs** | **2.102 μs** |  **2.733 μs** | **1.2207** |   **7.66 KB** |
| GoMakeBoundedAsync     | False        | False        | 116.23 μs | 2.295 μs |  2.643 μs | 0.6104 |   4.04 KB |
| NativeBoundedAsync     | False        | False        | 114.83 μs | 2.177 μs |  2.235 μs | 0.6104 |   4.04 KB |
| NativeUnboundedAsync   | False        | False        | 104.05 μs | 1.964 μs |  3.830 μs | 0.9766 |      8 KB |
| GoMakePrioritizedAsync | False        | False        | 364.10 μs | 7.224 μs |  6.032 μs | 0.9766 |   7.29 KB |
| **GoMakeUnboundedAsync**   | **False**        | **True**         | **108.37 μs** | **2.158 μs** |  **2.806 μs** | **1.2207** |   **7.23 KB** |
| GoMakeBoundedAsync     | False        | True         | 113.95 μs | 1.941 μs |  1.721 μs | 0.4883 |   4.04 KB |
| NativeBoundedAsync     | False        | True         | 117.51 μs | 2.282 μs |  2.886 μs |      - |   4.04 KB |
| NativeUnboundedAsync   | False        | True         | 107.80 μs | 2.151 μs |  4.144 μs | 1.2207 |   7.18 KB |
| GoMakePrioritizedAsync | False        | True         | 392.15 μs | 7.511 μs |  7.377 μs | 0.9766 |   7.35 KB |
| **GoMakeUnboundedAsync**   | **True**         | **False**        |  **69.78 μs** | **1.241 μs** |  **1.100 μs** | **1.4648** |   **9.14 KB** |
| GoMakeBoundedAsync     | True         | False        | 113.88 μs | 1.477 μs |  1.309 μs | 0.6104 |   4.04 KB |
| NativeBoundedAsync     | True         | False        | 114.36 μs | 1.795 μs |  1.679 μs | 0.6104 |   4.04 KB |
| NativeUnboundedAsync   | True         | False        |  70.27 μs | 1.381 μs |  2.821 μs | 1.5869 |   9.44 KB |
| GoMakePrioritizedAsync | True         | False        | 390.79 μs | 7.792 μs | 11.663 μs | 0.9766 |    7.2 KB |
| **GoMakeUnboundedAsync**   | **True**         | **True**         |  **67.40 μs** | **1.305 μs** |  **1.743 μs** | **1.4648** |   **9.37 KB** |
| GoMakeBoundedAsync     | True         | True         | 115.01 μs | 1.852 μs |  1.733 μs | 0.6104 |   4.04 KB |
| NativeBoundedAsync     | True         | True         | 112.93 μs | 1.654 μs |  1.466 μs | 0.6104 |   4.04 KB |
| NativeUnboundedAsync   | True         | True         |  74.27 μs | 1.461 μs |  2.401 μs | 1.4648 |   8.87 KB |
| GoMakePrioritizedAsync | True         | True         | 373.62 μs | 7.266 μs | 10.421 μs | 0.9766 |    7.2 KB |
