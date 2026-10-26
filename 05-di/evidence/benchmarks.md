# Benchmarks (claims 3 and 4)

**Status: pending the lead's quiet-machine run.** These numbers were not taken here, because the machine was shared with other builds while this project was written. Timing claims need a quiet machine.

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

## To record after the run

- The four categories' `Mean`, `Ratio` and `Allocated`, with the BenchmarkDotNet environment header.
- Claim 4 holds if every `*_Ours` row has `Ratio ≤ 1.25`.
- On Linux CI, `--disasm` adds the JIT output for `Graph_Ours` against `Graph_Microsoft`, showing whether the expression-compiled delegate and the IL-emitted one end up as the same machine code.
