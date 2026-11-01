```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v4
  Job-OEWQXS : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v4

LaunchCount=3  

```
| Method                | Categories | Mean       | Error     | StdDev     | Median     | Min        | Max        | Ratio | RatioSD | Gen0   | Allocated | Alloc Ratio |
|---------------------- |----------- |-----------:|----------:|-----------:|-----------:|-----------:|-----------:|------:|--------:|-------:|----------:|------------:|
| Graph_Microsoft       | Graph      |  43.114 ns | 0.5465 ns |  1.7111 ns |  43.407 ns |  37.792 ns |  46.957 ns |  1.00 |    0.06 | 0.0008 |     112 B |        1.00 |
| Graph_Ours            | Graph      |  44.298 ns | 0.6050 ns |  1.9641 ns |  44.222 ns |  37.809 ns |  50.011 ns |  1.03 |    0.06 | 0.0008 |     112 B |        1.00 |
| Graph_Interpreted     | Graph      | 168.799 ns | 2.2998 ns | 11.4866 ns | 168.828 ns | 131.505 ns | 204.332 ns |  3.92 |    0.31 | 0.0012 |     192 B |        1.71 |
| Graph_New             | Graph      |  37.260 ns | 0.4972 ns |  1.9239 ns |  37.067 ns |  31.302 ns |  42.205 ns |  0.87 |    0.06 | 0.0008 |     112 B |        1.00 |
|                       |            |            |           |            |            |            |            |       |         |        |           |             |
| Scoped_Microsoft      | Scoped     |  27.267 ns | 0.5615 ns |  2.8455 ns |  26.730 ns |  18.657 ns |  35.748 ns |  1.01 |    0.15 |      - |         - |          NA |
| Scoped_Ours           | Scoped     |  11.263 ns | 0.3718 ns |  1.9047 ns |  10.751 ns |   7.060 ns |  17.719 ns |  0.42 |    0.08 |      - |         - |          NA |
| Scoped_Interpreted    | Scoped     |  10.547 ns | 0.3369 ns |  1.7197 ns |  10.263 ns |   6.465 ns |  16.937 ns |  0.39 |    0.08 |      - |         - |          NA |
|                       |            |            |           |            |            |            |            |       |         |        |           |             |
| Singleton_Microsoft   | Singleton  |   6.650 ns | 0.1848 ns |  0.9347 ns |   6.513 ns |   4.176 ns |   9.885 ns |  1.02 |    0.20 |      - |         - |          NA |
| Singleton_Ours        | Singleton  |   8.512 ns | 0.1589 ns |  0.7994 ns |   8.513 ns |   5.642 ns |  10.524 ns |  1.31 |    0.22 |      - |         - |          NA |
| Singleton_Interpreted | Singleton  |   8.401 ns | 0.1875 ns |  0.8139 ns |   8.409 ns |   5.575 ns |  10.306 ns |  1.29 |    0.22 |      - |         - |          NA |
|                       |            |            |           |            |            |            |            |       |         |        |           |             |
| Transient_Microsoft   | Transient  |  16.463 ns | 0.2312 ns |  1.0305 ns |  16.535 ns |  12.662 ns |  18.967 ns |  1.00 |    0.09 | 0.0001 |      24 B |        1.00 |
| Transient_Ours        | Transient  |  17.441 ns | 0.3272 ns |  1.3961 ns |  17.533 ns |  12.419 ns |  20.744 ns |  1.06 |    0.11 | 0.0001 |      24 B |        1.00 |
| Transient_Interpreted | Transient  |  36.216 ns | 0.4991 ns |  2.5656 ns |  36.047 ns |  27.102 ns |  42.627 ns |  2.21 |    0.21 | 0.0001 |      24 B |        1.00 |
