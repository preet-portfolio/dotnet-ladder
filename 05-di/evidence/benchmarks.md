# Benchmarks (claims 3 and 4)

**Status: measured on 2026-11-01** on a shared cloud VM (below), with Microsoft's `ServiceProvider` 10.0.13 and ours in the same BenchmarkDotNet run, three launches per benchmark. Claim 3 held; claim 4 held for scoped, transient and the graph, and **failed for singletons** (1.31×).

## Result

```
BenchmarkDotNet v0.15.8, Linux Ubuntu 24.04.5 LTS (Noble Numbat)
Intel Xeon Processor 2.10GHz, 1 CPU, 4 logical and 4 physical cores
.NET SDK 10.0.402
  [Host]     : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v4
  Job-OEWQXS : .NET 10.0.13 (10.0.13, 10.0.1326.50504), X64 RyuJIT x86-64-v4

LaunchCount=3
```

Machine: KVM guest, 4 vCPUs (`lscpu`: Intel Xeon Processor @ 2.10GHz, family 6 model 207, no PMU exposed to the guest), 15 GB RAM, kernel 6.18. Nothing else heavy ran during the run (load average 0.6 to 1.0, the benchmark itself). Raw tables: [`bdn/`](bdn/).

| Category | Microsoft | Ours (compiled) | Ratio (RatioSD) | Ours, interpreted | Allocated (MS / ours) |
|---|---:|---:|---:|---:|---:|
| Singleton | 6.65 ns | 8.51 ns | **1.31** (0.22) | 8.40 ns | 0 / 0 B |
| Scoped | 27.27 ns | 11.26 ns | 0.42 (0.08) | 10.55 ns | 0 / 0 B |
| Transient | 16.46 ns | 17.44 ns | 1.06 (0.11) | 36.22 ns | 24 / 24 B |
| Graph | 43.11 ns | 44.30 ns | 1.03 (0.06) | 168.80 ns | 112 / 112 B |
| Graph built with `new` | | | 0.87 of Microsoft | | 112 B |

### Noise

This is a shared VM, so the noise is recorded with the result. Per benchmark: number of measured iterations after outlier removal, coefficient of variation, and the interquartile range.

| Benchmark | N | CV | Q1–Q3 |
|---|---:|---:|---:|
| Singleton_Microsoft | 283 | 14.1% | 6.12–7.09 ns |
| Singleton_Ours | 280 | 9.4% | 8.14–8.97 ns |
| Scoped_Microsoft | 284 | 10.4% | 25.66–29.05 ns |
| Scoped_Ours | 290 | 16.9% | 9.88–12.38 ns (BenchmarkDotNet flags it bimodal) |
| Transient_Microsoft | 221 | 6.3% | 15.88–17.13 ns |
| Transient_Ours | 203 | 8.0% | 16.47–18.42 ns (flagged bimodal) |
| Graph_Microsoft | 112 | 4.0% | 42.00–44.19 ns |
| Graph_Ours | 120 | 4.4% | 42.90–45.79 ns |

The singleton result is not a noise artefact: the two interquartile ranges do not overlap, and mean (1.28×), median (1.31×) and minimum (1.35×) all put ours above the 1.25× bound. Transient and graph are within noise of Microsoft; scoped is clearly faster.

### Why the singleton is slower (not measured; a guess)

Both containers serve a warm singleton by a `ConcurrentDictionary` probe and a delegate that returns a captured value. Ours adds two `_disposed` checks (scope and container) and a validator null check before the delegate call. That is a few nanoseconds' worth of branches at most, which is the size of the gap, but no profile has confirmed it. Scoped is faster because ours reads an array slot the plan claimed, where Microsoft probes a per-scope dictionary.

## Command

```sh
dotnet run -c Release --project 05-di/bench/Ladder.DI.Bench -- --filter '*'
```

`bench/Ladder.DI.Bench/ResolutionBenchmarks.cs`. Every resolution goes through a long-lived scope's `IServiceProvider`, as a request handler's would. Four categories, each with Microsoft's `ServiceProvider` 10.0.13 as the baseline, so the `Ratio` column is claim 4:

| Category | Service | Rows |
|---|---|---|
| Singleton | `Singleton` (constructed by the container) | Microsoft, ours (compiled), ours (interpreted) |
| Scoped | `Scoped(Singleton)` | same |
| Transient | `Transient` (no dependencies) | same |
| Graph | `Top(Middle(Bottom, Singleton), Bottom)`, all transient except the singleton | same, plus `Graph_New`: the graph built with `new` |

Setup resolves every service ten times, then sleeps 500 ms. Microsoft's container compiles a service on a thread-pool thread after its second resolution and swaps the result in when it's done; the sleep makes sure both containers are measured compiled.

## Verified so far

The project builds, and `--job Dry` runs all 13 benchmarks (one cold invocation each, so the times are meaningless). The `Allocated` column of that dry run already agrees with claim 3 and with the unit tests:

| Category | Microsoft | Ours (compiled) | Ours (interpreted) | `new` |
|---|---:|---:|---:|---:|
| Singleton | 0 B | 0 B | 0 B | |
| Scoped | 0 B | 0 B | 0 B | |
| Transient | 24 B | 24 B | 24 B | |
| Graph | 112 B | 112 B | 192 B | 112 B |

The interpreter's extra 80 B in the graph is the argument arrays for two constructor calls through reflection, which is why warm services are compiled.

Claim 3 is also checked without BenchmarkDotNet by `tests/Ladder.DI.Tests/AllocationTests.cs`, which counts bytes with `GC.GetAllocatedBytesForCurrentThread` over 1,000 warm resolutions. It passes in Debug and Release: 0 B per call for a singleton (from the container and from a scope, both engines) and for a scoped service (both engines), and for a three-level transient graph exactly the bytes of the same graph built with `new`.

## What the run was asked to record

- The four categories' `Mean`, `Ratio` and `Allocated`, with the BenchmarkDotNet environment header.
- Claim 4 holds if every `*_Ours` row has `Ratio ≤ 1.25`.
- On Linux CI, `--disasm` adds the JIT output for `Graph_Ours` against `Graph_Microsoft`, showing whether the expression-compiled delegate and the IL-emitted one end up as the same machine code.
