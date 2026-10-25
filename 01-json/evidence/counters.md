# Runtime capture: dotnet-counters during a 30 s tokenize loop

## How it was captured

```
dotnet dotnet-counters collect --format csv -o evidence/counters.csv --refresh-interval 1 \
  --counters "System.Runtime[dotnet.gc.heap.total_allocated,dotnet.gc.collections,dotnet.gc.last_collection.heap.size,dotnet.process.cpu.time,dotnet.gc.pause.time]" \
  -- dotnet bench/Ladder.Json.Bench/bin/Release/net10.0/Ladder.Json.Bench.dll soak 30
```

`soak 30` loads the three corpus files once, then tokenizes them in a loop for 30 seconds on one thread. Raw samples: [`counters.csv`](counters.csv) (one row per counter per second).

## What it shows

| Counter (30 one-second samples) | Value |
|---|---|
| `dotnet.gc.collections`, gen0 / gen1 / gen2 | 0 / 0 / 0, every sample |
| `dotnet.gc.pause.time` | 0 s |
| `dotnet.process.cpu.time` (user) | ~1.0 s per second: one core saturated, as expected for a single-threaded loop |
| `dotnet.gc.heap.total_allocated` rate | **16–41 KB/s, ~759 KB over the run** |

No GC ran for the whole loop. Allocation was not zero, though, so that needed explaining before it could count as evidence for the zero-allocation claim.

## Explaining the allocation rate

The tokenizer loop isn't the source. The same binary, run **without** dotnet-counters attached, prints the process-wide `GC.GetTotalAllocatedBytes(precise: true)` at the end:

| Run | Tokens | Total allocated |
|---|---|---|
| `soak 2` | 133,786,224 | 4,686,072 bytes |
| `soak 10` | 739,540,516 | 4,686,080 bytes |
| `soak 20` | 1,482,459,472 | 4,686,080 bytes |

The corpus files are 4,609,769 bytes and the remaining ~76 KB is startup. About eleven times more tokens produced 8 more bytes, and that 8-byte difference is not explained. The 16–41 KB/s seen under dotnet-counters is the in-process metrics listener that dotnet-counters enables, i.e. the measurement disturbing what it measures. For per-operation allocation, the BenchmarkDotNet `Allocated` column and the `Tokenizing_allocates_nothing` unit test are the better instruments. A counters session only shows the process as a whole.

Throughput from the soak runs: 0.85–0.94 GiB/s on one Apple M1 core. Captured on the final build; an earlier capture before the `ValueSpan` fix showed the same pattern (24–49 KB/s under counters, flat totals without).
