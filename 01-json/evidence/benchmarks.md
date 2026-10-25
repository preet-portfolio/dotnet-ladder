# Benchmarks

Raw BenchmarkDotNet output, `dotnet run -c Release --project bench/Ladder.Json.Bench -- --filter '*'`. Corpus: nativejson-benchmark @ 478d572.

## Final run (code as committed)

```
BenchmarkDotNet v0.15.8, macOS 27.2 (26B118) [Darwin 27.2.0]
Apple M1, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.13 (10.0.13, 10.0.1326.50504), Arm64 RyuJIT armv8.0-a

```

| Method                                 | File              | Mean        | Error    | StdDev   | Ratio | Allocated | Alloc Ratio |
|--------------------------------------- |------------------ |------------:|---------:|---------:|------:|----------:|------------:|
| Ladder_JsonTokenizer                   | canada.json       | 1,938.24 us | 1.680 us | 1.490 us |  0.51 |         - |          NA |
| Stj_Utf8JsonReader                     | canada.json       | 3,781.90 us | 2.694 us | 2.388 us |  1.00 |         - |          NA |
| Stj_Utf8JsonReader_plus_Utf8Validation | canada.json       | 3,740.05 us | 1.914 us | 1.598 us |  0.99 |         - |          NA |
| Utf8Validation_only                    | canada.json       |    64.71 us | 0.089 us | 0.079 us |  0.02 |         - |          NA |
|                                        |                   |             |          |          |       |           |             |
| Ladder_JsonTokenizer                   | citm_catalog.json | 1,614.88 us | 2.207 us | 2.065 us |  0.54 |         - |          NA |
| Stj_Utf8JsonReader                     | citm_catalog.json | 2,983.08 us | 9.175 us | 7.662 us |  1.00 |         - |          NA |
| Stj_Utf8JsonReader_plus_Utf8Validation | citm_catalog.json | 3,010.93 us | 3.376 us | 2.993 us |  1.01 |         - |          NA |
| Utf8Validation_only                    | citm_catalog.json |    40.30 us | 0.027 us | 0.024 us |  0.01 |         - |          NA |
|                                        |                   |             |          |          |       |           |             |
| Ladder_JsonTokenizer                   | twitter.json      |   525.96 us | 1.379 us | 1.151 us |  0.64 |         - |          NA |
| Stj_Utf8JsonReader                     | twitter.json      |   821.65 us | 1.321 us | 1.236 us |  1.00 |         - |          NA |
| Stj_Utf8JsonReader_plus_Utf8Validation | twitter.json      |   862.87 us | 1.102 us | 0.860 us |  1.05 |         - |          NA |
| Utf8Validation_only                    | twitter.json      |    44.45 us | 0.145 us | 0.128 us |  0.05 |         - |          NA |

## Earlier runs, before the ValueSpan fix

Kept for the run-to-run comparison: Stj_Utf8JsonReader on canada.json measured 3,267 us, 3,975 us and 3,782 us across three processes, while Ladder_JsonTokenizer stayed between 1,904 and 1,938 us.

| Method                                 | File              | Mean        | Error     | StdDev    | Ratio | Allocated | Alloc Ratio |
|--------------------------------------- |------------------ |------------:|----------:|----------:|------:|----------:|------------:|
| Ladder_JsonTokenizer                   | canada.json       | 1,914.36 us |  3.542 us |  3.313 us |  0.48 |         - |          NA |
| Stj_Utf8JsonReader                     | canada.json       | 3,974.84 us | 11.601 us | 10.284 us |  1.00 |         - |          NA |
| Stj_Utf8JsonReader_plus_Utf8Validation | canada.json       | 3,768.65 us |  8.456 us |  6.602 us |  0.95 |         - |          NA |
| Utf8Validation_only                    | canada.json       |    64.23 us |  0.149 us |  0.139 us |  0.02 |         - |          NA |
|                                        |                   |             |           |           |       |           |             |
| Ladder_JsonTokenizer                   | citm_catalog.json | 1,595.82 us |  4.243 us |  3.969 us |  0.54 |         - |          NA |
| Stj_Utf8JsonReader                     | citm_catalog.json | 2,973.73 us | 27.519 us | 25.741 us |  1.00 |         - |          NA |
| Stj_Utf8JsonReader_plus_Utf8Validation | citm_catalog.json | 2,990.54 us |  8.894 us |  8.320 us |  1.01 |         - |          NA |
| Utf8Validation_only                    | citm_catalog.json |    40.04 us |  0.069 us |  0.058 us |  0.01 |         - |          NA |
|                                        |                   |             |           |           |       |           |             |
| Ladder_JsonTokenizer                   | twitter.json      |   522.96 us |  1.561 us |  1.219 us |  0.64 |         - |          NA |
| Stj_Utf8JsonReader                     | twitter.json      |   816.46 us |  1.580 us |  1.477 us |  1.00 |         - |          NA |
| Stj_Utf8JsonReader_plus_Utf8Validation | twitter.json      |   858.37 us |  1.889 us |  1.675 us |  1.05 |         - |          NA |
| Utf8Validation_only                    | twitter.json      |    44.07 us |  0.078 us |  0.073 us |  0.05 |         - |          NA |

| Method                                 | File              | Mean       | Error    | StdDev   | Ratio | Allocated | Alloc Ratio |
|--------------------------------------- |------------------ |-----------:|---------:|---------:|------:|----------:|------------:|
| Ladder_JsonTokenizer                   | canada.json       | 1,904.5 us |  3.59 us |  3.00 us |  0.58 |         - |          NA |
| Stj_Utf8JsonReader                     | canada.json       | 3,267.4 us |  8.84 us |  8.27 us |  1.00 |         - |          NA |
| Stj_Utf8JsonReader_plus_Utf8Validation | canada.json       | 4,138.1 us |  9.56 us |  8.94 us |  1.27 |         - |          NA |
|                                        |                   |            |          |          |       |           |             |
| Ladder_JsonTokenizer                   | citm_catalog.json | 1,599.5 us | 13.89 us | 10.85 us |  0.54 |         - |          NA |
| Stj_Utf8JsonReader                     | citm_catalog.json | 2,950.5 us |  5.38 us |  5.03 us |  1.00 |         - |          NA |
| Stj_Utf8JsonReader_plus_Utf8Validation | citm_catalog.json | 3,076.6 us |  4.21 us |  3.94 us |  1.04 |         - |          NA |
|                                        |                   |            |          |          |       |           |             |
| Ladder_JsonTokenizer                   | twitter.json      |   522.1 us |  1.25 us |  1.05 us |  0.64 |         - |          NA |
| Stj_Utf8JsonReader                     | twitter.json      |   815.1 us |  1.12 us |  1.04 us |  1.00 |         - |          NA |
| Stj_Utf8JsonReader_plus_Utf8Validation | twitter.json      |   860.6 us |  2.05 us |  1.92 us |  1.06 |         - |          NA |
