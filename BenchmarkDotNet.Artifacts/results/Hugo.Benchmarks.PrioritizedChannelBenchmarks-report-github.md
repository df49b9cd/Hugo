```

BenchmarkDotNet v0.15.4, macOS 26.0.1 (25A362) [Darwin 25.0.0]
Apple M1 Max 2.40GHz, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100-rc.2.25502.107
  [Host]     : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2
  DefaultJob : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2


```
| Method                        | PriorityLevels | UseBoundedCapacity | Mean     | Error    | StdDev   | Allocated |
|------------------------------ |--------------- |------------------- |---------:|---------:|---------:|----------:|
| **PrioritizedChannelAsync**       | **3**              | **False**              | **18.24 ms** | **0.129 ms** | **0.107 ms** |  **34.92 KB** |
| StandardBoundedChannelAsync   | 3              | False              | 20.31 ms | 0.145 ms | 0.136 ms |   3.87 KB |
| StandardUnboundedChannelAsync | 3              | False              | 18.53 ms | 0.077 ms | 0.072 ms |   36.5 KB |
| **PrioritizedChannelAsync**       | **3**              | **True**               | **19.25 ms** | **0.035 ms** | **0.027 ms** |   **6.54 KB** |
| StandardBoundedChannelAsync   | 3              | True               | 20.75 ms | 0.092 ms | 0.086 ms |   3.87 KB |
| StandardUnboundedChannelAsync | 3              | True               | 18.67 ms | 0.091 ms | 0.081 ms |   36.5 KB |
| **PrioritizedChannelAsync**       | **5**              | **False**              | **18.25 ms** | **0.138 ms** | **0.129 ms** |  **35.93 KB** |
| StandardBoundedChannelAsync   | 5              | False              | 20.60 ms | 0.108 ms | 0.101 ms |   5.89 KB |
| StandardUnboundedChannelAsync | 5              | False              | 18.18 ms | 0.136 ms | 0.120 ms |   36.5 KB |
| **PrioritizedChannelAsync**       | **5**              | **True**               | **19.41 ms** | **0.032 ms** | **0.027 ms** |   **9.79 KB** |
| StandardBoundedChannelAsync   | 5              | True               | 20.73 ms | 0.118 ms | 0.104 ms |   5.89 KB |
| StandardUnboundedChannelAsync | 5              | True               | 18.28 ms | 0.066 ms | 0.058 ms |   36.5 KB |
