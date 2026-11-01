# Why our tokenizer is faster than `Utf8JsonReader` (claims 4–7)

**Short answer: we do not know most of it.** The three pieces of bookkeeping that we skip and `Utf8JsonReader` does
explain **11–44%** of the gap, depending on the file. Claim 4 predicted at least 50% and **failed**. A per-token-kind
cost model also **failed** (claim 6). It misses the gap by −61% on twitter and −74% on citm. On these two files, the
costs do not add up token by token. The one file the model handles is canada: almost all of it is numbers, and the
model gets its gap within −13%.

## Setup

- Machine: shared cloud VM, Intel Xeon Processor @ 2.10GHz (family 6, model 207), 4 vCPUs (KVM guest), 15 GB RAM,
  kernel 6.18.44, Ubuntu 24.04.5. .NET SDK 10.0.402, runtime 10.0.13, RyuJIT x86-64-v4.
- The VM exposes no PMU, so there are no hardware counters. This is why claim 7 stays pending.
- Benchmarks: BenchmarkDotNet 0.15.8, `--launchCount 2`, default job. Load average was about 0.8 and nothing else
  heavy was running.
- Noise: the coefficient of variation (StdDev/Mean) of every row was **3–10%**. The 99.9% error of the mean was 1.5–2.5%.
  Effects under about 3% are noise. The full rows, including StdDev, Median, Min and Max, are in
  [`why-faster/`](why-faster/).
- These timings are not comparable with the Apple M1 numbers in [`benchmarks.md`](benchmarks.md). The ratios are similar
  for citm and twitter. canada differs: we take 0.43× the time of `Utf8JsonReader` here, against 0.51× on the M1.

## Ablation (claims 4 and 5)

`AblationTokenizer<T>` (in `bench/Ladder.Json.Bench/AblationTokenizer.cs`) is a copy of `JsonTokenizer`. Static
interface flags switch on `Utf8JsonReader`'s extra work, so the JIT removes the paths that are switched off:

- **Lines:** line number and byte-in-line tracking during whitespace skipping, plus a running consumed count.
- **Rollback:** a snapshot of reader state before each token, as `Utf8JsonReader` takes for non-final buffers.
- **Options:** the comment-handling and trailing-comma checks at each value and after each comma.

With every flag off, the copy runs 3–5% slower than the real tokenizer. The gap is measured against the copy, so this
difference does not count as closed.

"Gap closed" is (variant − copy) / (`Utf8JsonReader` − copy), computed from means. Medians agree within 2 points.

| File | Gap (µs) | Lines | Rollback | Options | All three | Claim 4 (≥ 50%) | Claim 5 (Lines < 25%) |
|---|---:|---:|---:|---:|---:|---|---|
| canada.json | 3,005 | 0.3% | 6.1% | 1.0% | **11.5%** | Failed | Held |
| citm_catalog.json | 2,067 | 18.5% | 0.5% | −2.1% | **34.7%** | Failed | Held |
| twitter.json | 211 | 18.8% | 22.1% | 3.3% | **44.2%** | Failed | Held |

What these numbers mean:

- **Lines** only costs where there is whitespace to scan. canada has 24 whitespace bytes, and Lines closes 0.3% of its
  gap. citm has 1.2 MB of indentation, and Lines closes 18.5%.
- **Rollback** costs most where tokens are short and dense. On twitter it closes 22%; on citm, which has fewer tokens
  per byte, it closes 0.5%.
- **Options** is inside the noise on every file.
- On citm the three together close more than the sum of their parts: about 17 points summed, 34.7 measured.
  Our guess at the reason is that adding all three at once costs more registers or a larger inlining budget, not just
  the work each one adds [Guessing]. We did not look at the generated assembly to check.

## Per-kind cost model (claim 6)

`bench/cost_model.py` fits costs in nanoseconds from eight synthetic documents of about 1 MB, each made of one token
kind (`TokenKindBenchmarks.Generate`, seed 20261101):

- per integer and per double;
- per literal and per empty container;
- per whitespace byte;
- per string, plus a cost per string byte;
- per property name.

It fits these separately for ours and for `Utf8JsonReader`. Then it predicts each corpus file from the file's token
counts in [`why-faster/token-counts.csv`](why-faster/token-counts.csv). Full output:
[`why-faster/cost-model.md`](why-faster/cost-model.md).

| File | Gap predicted (µs) | Gap measured (µs) | Error | Claim 6 (±25%) |
|---|---:|---:|---:|---|
| twitter.json | 90 | 232 | −61% | Failed |
| citm_catalog.json | 549 | 2,118 | −74% | Failed |
| canada.json | 2,692 | 3,090 | −13% | Held on this file |

The model is wrong in two ways that both show its structure is wrong, not just its numbers:

- **The property fit is negative for `Utf8JsonReader`** (−3.4 ns). In the Properties document, `Utf8JsonReader` is
  cheaper than the sum of its parts (a property name plus a literal, inside nested containers). So its costs depend on
  the order tokens come in, not only on how many there are.
- **It predicts our time on citm from the synthetic documents as slower than we measured** (1,697 µs predicted, 1,518 µs
  measured). Meanwhile it predicts `Utf8JsonReader` on citm as much faster than measured (2,246 vs 3,637 µs). The extra
  cost `Utf8JsonReader` pays on real documents is not in any of the one-kind documents.

## CPU profiles

- Captured with `dotnet-trace collect --profile dotnet-common,dotnet-sampled-thread-time` (about 100 Hz, 20 s each), on
  `soak` (ours) and `soak-stj`, with one file per run. Summary: [`why-faster/profiles.txt`](why-faster/profiles.txt).
- The JIT inlines `Read()` into the counting loop, so the largest bucket for each side is the loop itself: 26–49% of
  samples.
- The rest has the same shape on both sides. The code that runs after each value is the biggest named function:
  - ours: `ReadCommaOrClose`, 24% (citm) and 36% (twitter);
  - `Utf8JsonReader`: `ConsumeNextTokenOrRollback`, 35% and 46%.
- We spend more on UTF-8 validation of twitter's non-ASCII strings than `Utf8JsonReader` does: 13% of samples in
  `Utf8Utility.GetPointerToFirstInvalidByte`, against 8% in `ConsumeStringAndValidate`. So validation is not where we
  win.
- At this sampling rate, and with this much inlining, the profiles do not split the gap any further. They do show that
  `Utf8JsonReader` spends more time in the code that runs after each value than we do. That path is where the
  rollback snapshot is taken.

## What is unknown

- Most of the gap: 56–89% of it, depending on the file, is not explained by the three suspects.
- Whether the remainder is fewer instructions or better instruction-level parallelism. That needs instructions-per-byte
  and IPC from hardware counters (claim 7), and this VM has no PMU. To answer it, rerun
  `perf stat -e instructions,cycles` on bare metal against `soak` and `soak-stj`.
- Why the costs interact on citm. Finding out needs the JIT disassembly (`DOTNET_JitDisasm=Read`) of `AblationTokenizer`
  with all three flags on, compared with each flag on alone.
