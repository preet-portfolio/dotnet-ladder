```

BenchmarkDotNet v0.15.8, macOS 27.2 (26B123) [Darwin 27.2.0]
Apple M1, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.402
  [Host]   : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  ShortRun : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

Job=ShortRun  IterationCount=3  LaunchCount=1  
WarmupCount=3  

```
| Method   | Mean     | Error     | StdDev    | Gen0   | Allocated |
|--------- |---------:|----------:|----------:|-------:|----------:|
| Get_x16  | 9.362 μs | 37.554 μs | 2.0585 μs |      - |         - |
| Incr_x16 | 2.745 μs |  2.206 μs | 0.1209 μs |      - |         - |
| Set_x16  | 4.771 μs |  3.670 μs | 0.2012 μs | 0.3242 |    2048 B |
