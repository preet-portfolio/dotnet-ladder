```

BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v4
  Job-BWELMW : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v4

LaunchCount=2  

```
| Method               | File              | Mean       | Error     | StdDev    | Median     | Min        | Max        | Ratio | RatioSD | Allocated | Alloc Ratio |
|--------------------- |------------------ |-----------:|----------:|----------:|-----------:|-----------:|-----------:|------:|--------:|----------:|------------:|
| **Stj_Utf8JsonReader**   | **canada.json**       | **5,374.9 μs** | **120.20 μs** | **502.33 μs** | **5,455.1 μs** | **3,891.8 μs** | **6,365.6 μs** |  **1.01** |    **0.14** |         **-** |          **NA** |
| Ladder_JsonTokenizer | canada.json       | 2,285.1 μs |  40.58 μs | 169.57 μs | 2,283.3 μs | 1,724.0 μs | 2,715.1 μs |  0.43 |    0.05 |         - |          NA |
| Ablation_Baseline    | canada.json       | 2,370.1 μs |  37.59 μs | 146.33 μs | 2,369.8 μs | 1,822.3 μs | 2,738.4 μs |  0.45 |    0.05 |         - |          NA |
| Ablation_Lines       | canada.json       | 2,380.2 μs |  33.92 μs | 133.25 μs | 2,380.2 μs | 1,794.9 μs | 2,701.8 μs |  0.45 |    0.05 |         - |          NA |
| Ablation_Rollback    | canada.json       | 2,554.4 μs |  36.85 μs | 148.60 μs | 2,545.3 μs | 1,974.7 μs | 2,959.8 μs |  0.48 |    0.06 |         - |          NA |
| Ablation_Options     | canada.json       | 2,399.9 μs |  39.52 μs | 165.16 μs | 2,414.2 μs | 1,805.0 μs | 2,787.1 μs |  0.45 |    0.06 |         - |          NA |
| Ablation_AllThree    | canada.json       | 2,716.7 μs |  49.88 μs | 209.56 μs | 2,696.0 μs | 2,071.8 μs | 3,214.7 μs |  0.51 |    0.07 |         - |          NA |
|                      |                   |            |           |           |            |            |            |       |         |           |             |
| **Stj_Utf8JsonReader**   | **citm_catalog.json** | **3,636.6 μs** |  **78.83 μs** | **271.18 μs** | **3,703.1 μs** | **2,818.6 μs** | **4,028.7 μs** |  **1.01** |    **0.11** |         **-** |          **NA** |
| Ladder_JsonTokenizer | citm_catalog.json | 1,518.1 μs |  29.86 μs | 116.94 μs | 1,522.8 μs | 1,268.6 μs | 1,833.3 μs |  0.42 |    0.05 |         - |          NA |
| Ablation_Baseline    | citm_catalog.json | 1,569.9 μs |  26.03 μs | 109.09 μs | 1,568.2 μs | 1,177.1 μs | 1,842.3 μs |  0.43 |    0.05 |         - |          NA |
| Ablation_Lines       | citm_catalog.json | 1,952.8 μs |  38.12 μs | 158.88 μs | 1,932.2 μs | 1,400.7 μs | 2,386.6 μs |  0.54 |    0.06 |         - |          NA |
| Ablation_Rollback    | citm_catalog.json | 1,581.1 μs |  25.65 μs | 106.33 μs | 1,577.4 μs | 1,189.7 μs | 1,878.1 μs |  0.44 |    0.05 |         - |          NA |
| Ablation_Options     | citm_catalog.json | 1,527.2 μs |  29.01 μs | 121.22 μs | 1,519.7 μs | 1,132.5 μs | 1,823.8 μs |  0.42 |    0.05 |         - |          NA |
| Ablation_AllThree    | citm_catalog.json | 2,287.5 μs |  54.22 μs | 228.96 μs | 2,264.4 μs | 1,652.3 μs | 2,815.2 μs |  0.63 |    0.08 |         - |          NA |
|                      |                   |            |           |           |            |            |            |       |         |           |             |
| **Stj_Utf8JsonReader**   | **twitter.json**      |   **729.0 μs** |  **12.19 μs** |  **47.75 μs** |   **726.7 μs** |   **561.2 μs** |   **830.9 μs** |  **1.00** |    **0.10** |         **-** |          **NA** |
| Ladder_JsonTokenizer | twitter.json      |   497.4 μs |   7.35 μs |  30.80 μs |   494.0 μs |   384.3 μs |   569.4 μs |  0.69 |    0.07 |         - |          NA |
| Ablation_Baseline    | twitter.json      |   518.1 μs |   8.46 μs |  31.27 μs |   520.2 μs |   398.9 μs |   578.9 μs |  0.71 |    0.07 |         - |          NA |
| Ablation_Lines       | twitter.json      |   557.7 μs |  10.46 μs |  42.79 μs |   561.0 μs |   421.6 μs |   642.0 μs |  0.77 |    0.08 |         - |          NA |
| Ablation_Rollback    | twitter.json      |   564.8 μs |   8.25 μs |  33.94 μs |   564.8 μs |   426.6 μs |   654.5 μs |  0.78 |    0.07 |         - |          NA |
| Ablation_Options     | twitter.json      |   525.0 μs |   8.66 μs |  36.40 μs |   528.6 μs |   392.3 μs |   604.9 μs |  0.72 |    0.07 |         - |          NA |
| Ablation_AllThree    | twitter.json      |   611.3 μs |  10.13 μs |  39.06 μs |   609.9 μs |   480.7 μs |   690.3 μs |  0.84 |    0.08 |         - |          NA |
