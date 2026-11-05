# Coverage-guided fuzzing (claim 9)

**Result: held, with one change to the seeds** (see "Seeds" below). The run lasted 4.08 CPU-hours: 86 million inputs
and 163 full cycles of the queue. It found **0 crashes and 0 hangs**. No exception escaped `RespParser`. Every input
parsed to the same log of requests, statuses, errors and consumed offsets in three cases: arriving whole, one byte per
segment, and split at cut points chosen by the input. The final corpus covers **100% of `RespParser`'s lines and
branches**.

## How it was run

```sh
tools/fuzz/run-afl.sh 02-resp/fuzz/Ladder.Resp.Fuzz Ladder.Resp.dll <seed dir> <out dir> 14700 3
```

- **Harness:** [`fuzz/Ladder.Resp.Fuzz/Program.cs`](../fuzz/Ladder.Resp.Fuzz/Program.cs). It mirrors the server's process
  loop and calls `RespParser.TryRead` until the input runs out or the protocol is violated. Each call is logged with its
  status, its arguments and `reader.Consumed`. The logs of the three splits must be equal; otherwise, or if an
  exception escapes, it is a crash.
- **Seeds:** 240 files of 4 whole commands each, cut by [`fuzz/make_seeds.py`](../fuzz/make_seeds.py) from four
  captured request streams, 60 files from each:
  - the Twitter trace replay, 2020Mar/cluster022;
  - the Twitter trace replay, cluster014 (the commands of claim 8, pinned in [DATA-SOURCES.md](../../DATA-SOURCES.md));
  - the GeoNames `cities15000` load of claim 4;
  - the open-loop generator's `GET`/`SET` traffic.
- **Change from the claim:** the claim names "the Valkey test suite's requests" as a seed source. They were not
  captured, and the load generator's traffic took their place. So the seeds lack the suite's unusual commands: inline
  commands, very large arguments, binary keys. The fuzzer had to find those shapes itself. Coverage shows it reached
  every parser branch anyway, including the inline-command and size-limit paths.
- **Tools:**
  - AFL++ 4.09c, with `-t 10000 -m none -V 14700`;
  - SharpFuzz 2.3.0, which instruments `Ladder.Resp.dll`;
  - .NET 10.0.13, Release build.
- **Machine:** the shared cloud VM, an Intel Xeon Processor @ 2.10GHz with 4 vCPUs. This fuzzer was pinned to one vCPU
  (`-b 3`), and the 01 fuzzer and the Stryker runs were limited to the others.
- **Raw stats:** [`fuzzing/fuzzer_stats.txt`](fuzzing/fuzzer_stats.txt).

| | |
|---|---:|
| Run time | 14,700 s on 1 vCPU = **4.08 CPU-hours** |
| Executions | 86,473,640 (5,883/s; each input is parsed three times) |
| Queue cycles | 163 |
| Corpus at end | 377 inputs (from 240 seeds) |
| Edges found | 129 (stability 100%) |
| Last new path | 2 h 28 min before the end |
| Crashes / hangs | **0 / 0** |

## Coverage of the final corpus

Every queue entry was replayed through the harness's `check` mode under coverlet.console 6.0.4. This used an
uninstrumented Release build, and only `[Ladder.Resp]*` was measured. Raw data:
[`fuzzing/corpus-coverage.cobertura.xml`](fuzzing/corpus-coverage.cobertura.xml). The harness reaches only the parser
and its argument buffer, so the assembly-wide figure (5.2%) means nothing.

| File | Lines | Branches |
|---|---:|---:|
| `RespParser.cs` | 72/72 (**100%**) | 64/64 (**100%**) |
| `CommandArgs.cs` | 24/30 (80%) | 8/10 (80%) |

The 6 uncovered lines in `CommandArgs` are code the parser never calls:
- the indexer and `Memory(i)`: the harness reads arguments with `ToArrays()`;
- the `Add(ReadOnlyMemory<byte>)` overload, at `CommandArgs.cs:55–63`: only `EXEC` uses it, to replay queued commands.

The parser's own `Add(in ReadOnlySequence<byte>)` is fully covered. That includes growth of the argument array past 8
and the copy into the arena for arguments split across segments.

## After the parser fix (2026-11-02)

Mutation testing later found gaps in the parser's tests ([`stryker.md`](stryker.md)). Closing them against Valkey
9.1.2's source changed how `RespParser` reads length lines, so the 4-CPU-hour run above was of the **old** parser.
After the fix:
- **Corpus replay:** the old run's final corpus and seeds, 617 inputs, still pass the split oracle with no exception.
- **New run:** 1 more CPU-hour on the new parser, seeded with the old corpus. It made 16.8M executions and found
  **0 crashes and 0 hangs**. The corpus grew from 377 to 393 inputs and the edge count from 129 to 162, because the new
  code has more branches. Raw stats:
  [`fuzzing/fuzzer_stats-after-parser-fix.txt`](fuzzing/fuzzer_stats-after-parser-fix.txt).
- **Coverage:** the new corpus covers **100% of the new `RespParser`**, 93/93 lines and 90/90 branches. Neither that coverage report nor the corpus was kept, so this figure can't be re-derived.

So claim 9 holds for the old parser over 4.08 CPU-hours, and for the new parser over 1 CPU-hour plus a replay of the
old corpus. The 4-hour bound has not been re-run on the new code.

## What surprised us

- **100% coverage and a clean oracle did not mean a correct parser.** The split oracle checks consistency, not
  correctness. The parser accepted leading zeros and `-0`, and reported the wrong error for a bad `\r\n`. The fuzzer
  covered every one of those lines, and none of it could fail the oracle. Mutation testing, followed by tests taken from
  Valkey's source, found them.
- **The fuzzer stopped finding new paths after 1 h 37 min.** For the last 2 h 28 min it found nothing, while the 01
  fuzzer was still finding paths near its end. The parser's state is small. A one-line RESP header and a length-prefixed
  body give only 129 edges, and the corpus covers every line and branch of the parser. So the remaining CPU-hours checked the split
  oracle on new inputs; they did not open new code.
