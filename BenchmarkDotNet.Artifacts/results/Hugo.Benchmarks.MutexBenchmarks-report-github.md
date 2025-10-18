```

BenchmarkDotNet v0.15.4, macOS 26.0.1 (25A362) [Darwin 25.0.0]
Apple M1 Max 2.40GHz, 1 CPU, 10 logical and 10 physical cores
.NET SDK 10.0.100-rc.2.25502.107
  [Host]     : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2
  DefaultJob : .NET 10.0.0 (10.0.0-rc.2.25502.107, 10.0.25.50307), X64 RyuJIT x86-64-v2


```
| Method                   | TaskCount | Mean      | Error     | StdDev    | Ratio | Gen0    | Allocated | Alloc Ratio |
|------------------------- |---------- |----------:|----------:|----------:|------:|--------:|----------:|------------:|
| **HugoMutexAsync**           | **16**        |  **2.862 ms** | **0.0112 ms** | **0.0105 ms** |  **1.00** | **31.2500** | **192.97 KB** |       **1.000** |
| SemaphoreSlimAsync       | 16        |  2.676 ms | 0.0069 ms | 0.0058 ms |  0.93 | 19.5313 |    121 KB |       0.627 |
| HugoMutexEnterScopeAsync | 16        |  2.665 ms | 0.0153 ms | 0.0143 ms |  0.93 |       - |   1.83 KB |       0.009 |
| MonitorLockScopeAsync    | 16        |  2.726 ms | 0.0215 ms | 0.0201 ms |  0.95 |       - |   1.63 KB |       0.008 |
|                          |           |           |           |           |       |         |           |             |
| **HugoMutexAsync**           | **64**        | **11.616 ms** | **0.1124 ms** | **0.0939 ms** |  **1.00** | **90.9091** | **771.23 KB** |       **1.000** |
| SemaphoreSlimAsync       | 64        | 11.034 ms | 0.0223 ms | 0.0186 ms |  0.95 | 62.5000 | 483.23 KB |       0.627 |
| HugoMutexEnterScopeAsync | 64        | 10.292 ms | 0.0240 ms | 0.0201 ms |  0.89 |       - |    5.2 KB |       0.007 |
| MonitorLockScopeAsync    | 64        | 11.032 ms | 0.0102 ms | 0.0080 ms |  0.95 |       - |      5 KB |       0.006 |
