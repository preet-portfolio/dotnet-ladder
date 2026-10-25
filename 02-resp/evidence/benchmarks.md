# Benchmarks

## Claim 2: allocation per pipelined batch (BenchmarkDotNet)

`dotnet run -c Release --project bench/Ladder.Resp.Bench -- --filter '*'`. Each benchmark parses 16 pipelined requests from one buffer, executes them, and writes the replies, which is `Connection.ProcessBuffer` without the socket.

```
BenchmarkDotNet v0.15.8, macOS 27.2 (26B118) [Darwin 27.2.0]
Apple M1, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

```

| Method   | Mean     | Error     | StdDev    | Gen0   | Allocated |
|--------- |---------:|----------:|----------:|-------:|----------:|
| Get_x16  | 1.649 us | 0.0018 us | 0.0016 us |      - |         - |
| Incr_x16 | 1.555 us | 0.0041 us | 0.0036 us |      - |         - |
| Set_x16  | 2.108 us | 0.0317 us | 0.0265 us | 0.3242 |    2048 B |

`SET` allocates 128 B per command: the stored value object and its 64-byte array. That is the cost of storing the value; `GET` and `INCR` on existing keys allocate nothing.
