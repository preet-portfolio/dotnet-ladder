# 01 · JSON parser on `ReadOnlySpan<byte>`

**Concept:** parsing UTF-8 bytes straight from a span, with no strings, no allocations and no recursion.
**Spec:** RFC 8259 (JSON) and RFC 3629 (UTF-8).
**Source read first:** `dotnet/runtime` `release/10.0`: `System.Text.Json/.../Reader/Utf8JsonReader.cs` (`ConsumeString`, `TryGetNumber`, `BitStack` depth tracking) and `JsonReaderHelper.cs`.

## Decision

- `JsonTokenizer` is a `ref struct` over one complete buffer. Each `Read()` returns one token (kind, byte offset, raw value span). Unescaping and number conversion happen only when the caller asks (`TryGetString`, `TryGetDouble`, `TryGetInt64`).
- Malformed input never throws. `Read()` returns false and `Error` holds `{Offset, Reason}`. Only a bad constructor argument throws.
- Nesting is tracked in an inline 512-bit stack (`DepthBits`, an `[InlineArray]` of 8 `ulong`s), so depth costs one bit per level and no heap memory. Nothing recurses.
- Strings are scanned with a vectorized `IndexOfAny` for quote, backslash or control byte (the same approach as `Utf8JsonReader`). Then the whole raw string is checked with `Utf8.IsValid`. Escapes are ASCII, so that one call validates every literal segment between them.
- A UTF-8 byte-order mark is rejected. RFC 8259 §8.1 lets a parser ignore it, but rejecting it is the stricter choice.
- `JsonDocumentParser` builds a small tree from the tokens, with an explicit stack. It exists only to compare against System.Text.Json, and it is where unpaired surrogate escapes (`"\uD800"`) are rejected.

## Claims (1–3 committed with the first code in `a090cc8`, so git can't show they came first)

| # | Claim | Result |
|---|---|---|
| 1 | Tokenizing an in-memory document allocates 0 bytes | **Held.** BenchmarkDotNet `Allocated` = 0 B on all three files; `Tokenizing_allocates_nothing` unit test |
| 2 | JSONTestSuite: 100% of `y_` accepted, 100% of `n_` rejected, no crash | **Held.** 95/95 and 188/188, including `n_structure_100000_opening_arrays` |
| 3 | Throughput within 2× of `Utf8JsonReader` | **Held, with margin.** 0.51–0.64× of its time (ours is faster) |

### Why it is faster (claims committed in 8b487de, before the ablation code; measured on a Linux VM)

The README's hypothesis was that `Utf8JsonReader` is slower because it does work we don't. These claims test it.

| # | Claim | Result |
|---|---|---|
| 4 | The hypothesis explains **at least half** the gap: a benchmark-only copy of our tokenizer that also does `Utf8JsonReader`'s extra bookkeeping — line and byte-in-line tracking (4a), saving rollback state before each token for partial buffers (4b), and checking the comment and trailing-comma options (4c) — closes **≥ 50%** of the time gap to `Utf8JsonReader` on each corpus file | **Failed.** All three together close 11.5% (canada), 34.7% (citm) and 44.2% (twitter). See [`evidence/why-faster.md`](evidence/why-faster.md) |
| 5 | Prediction, not a goal: line and position tracking alone (4a) closes **< 25%** of the gap on each file | **Held.** 0.3%, 18.5% and 18.8% |
| 6 | On synthetic documents made of one token kind each, a per-kind cost model (ns per token, ours and STJ) predicts each corpus file's measured gap within **±25%** | **Failed.** −61% (twitter), −74% (citm); held only on canada (−13%). The costs are not additive: the fitted STJ cost per property name is negative |
| 7 | Hardware counters show STJ retires **≥ 1.5×** our instructions per byte | Pending (needs a PMU) |

### Coverage-guided fuzzing (committed before the fuzz harness was written)

| # | Claim | Result |
|---|---|---|
| 8 | SharpFuzz + AFL++ on `JsonTokenizer` and `JsonDocumentParser`, seeded with all 318 JSONTestSuite files, for **≥ 4 CPU-hours**: no exception escapes, and on every input that is valid UTF-8 our validity verdict equals `Utf8JsonReader`'s (the CsCheck property's oracle). Every crash becomes a regression test and a fix | **Held.** 4.08 CPU-hours, 469M inputs, 0 crashes, 0 hangs, 0 disagreements. The final corpus covers 94.9% of `JsonTokenizer`'s lines; the rest is misuse guards and one dead state. See [`evidence/fuzzing.md`](evidence/fuzzing.md) |

## Evidence

- External judge: [`judge/results/jsontestsuite.txt`](judge/results/jsontestsuite.txt) (one line per file, ours vs System.Text.Json). Rerun with `judge/run-judge.sh`, which downloads JSONTestSuite @ `1ef36fa` and the corpus @ `478d572`.
- Comparison with System.Text.Json: 0 tree mismatches on the 95 files both accept. Every disagreement and every `i_` result is in [`evidence/divergences.md`](evidence/divergences.md).
- Why it is faster: [`evidence/why-faster.md`](evidence/why-faster.md) (ablation, per-kind cost model, CPU profiles; Linux VM, raw BDN rows in [`evidence/why-faster/`](evidence/why-faster/)).
- Benchmarks (Apple M1, .NET 10.0.13): [`evidence/benchmarks.md`](evidence/benchmarks.md).

  | File | Ours | Utf8JsonReader | Ratio | Allocated |
  |---|---:|---:|---:|---:|
  | canada.json (2.2 MB, numbers) | 1,938 µs | 3,782 µs | 0.51 | 0 B |
  | citm_catalog.json (1.7 MB) | 1,615 µs | 2,983 µs | 0.54 | 0 B |
  | twitter.json (0.6 MB, non-ASCII) | 526 µs | 822 µs | 0.64 | 0 B |

- Runtime capture: [`evidence/counters.md`](evidence/counters.md). 0 GCs and 0 pause time during a 30 s loop. The explanation of the non-zero allocation rate dotnet-counters reported is there too.
- Tests: 101 xUnit tests. They include two CsCheck properties: 20,000 documents written by `Utf8JsonWriter` must parse back to the same values, and 50,000 randomly mutated documents must never throw and must agree with `Utf8JsonReader` on validity whenever the input is valid UTF-8.
- Fuzzing: [`evidence/fuzzing.md`](evidence/fuzzing.md).
- Mutation testing: **97.83%** at `058a8e5` (316 of 323 detected: 312 killed, 4 timeouts; 7 survivors, all equivalent). Raw report: [`evidence/stryker/mutation-report.md`](evidence/stryker/mutation-report.md). See [`evidence/stryker.md`](evidence/stryker.md).

## What surprised us

- **Coverage of the fuzz corpus found dead code.** `JsonTokenizer.Read()` handles an `Expect.PropertyName` state that nothing ever enters. Neither 469 million fuzz inputs nor the tests could reveal that; the coverage report of the final corpus did.
- **System.Text.Json accepts broken strings at parse time.** On 20 of the 35 `i_` files (10 with invalid UTF-8, 10 with unpaired `\u` surrogate escapes), `JsonDocument.Parse` succeeds and the error only appears later, as an `InvalidOperationException` when a string is read. `Utf8JsonReader` checks strings only for quotes, backslashes and control bytes. Ours rejects all 20 when it reads them.
- **UTF-8 validation is nearly free.** `Utf8.IsValid` over a whole file takes 40–65 µs, 2.5–8.5% of a tokenize pass. We expected the validation to be the reason we'd lose to STJ. It wasn't.
- **The obvious explanation for our speed covers less than half of it.** We assumed STJ is slower because it does work we don't: line and position tracking, rollback state for partial buffers, and comment and trailing-comma options. Adding all three to a copy of our tokenizer closes only 11–44% of the gap (claim 4 failed). On citm the three cost more together than the sum of each alone. Most of the gap is still unexplained; [`evidence/why-faster.md`](evidence/why-faster.md) lists what would settle it.
- **Counting tokens does not predict time.** A cost model fitted on one-kind documents is off by −61% and −74% on twitter and citm (claim 6 failed). STJ's cost depends on token order, not just token counts.
- **STJ's timing on canada.json moved up to 22% between runs** (3,267, 3,975 and 3,782 µs in three processes), while ours stayed within 1,904–1,938 µs. All runs are kept in the evidence.
- **dotnet-counters reported allocation that wasn't ours.** It showed 16–41 KB/s in the kept capture (a second capture, 24–49 KB/s, was not kept), which turned out to come from the in-process metrics listener it enables. A process-wide counter can't prove a per-call zero; BenchmarkDotNet and a unit test can.
- **The tooling needed checking too.** Stryker's first run reported a 0% score because it used the old VSTest runner while the repo runs on Microsoft.Testing.Platform (run not kept). Re-run at `058a8e5` with its default per-test coverage, Stryker reported 49.54% with 163 survivors; two of them, applied by hand, fail 6 and 8 tests. Running every test against every mutant gives 97.83%, so the config now sets `coverage-analysis: off`. BenchmarkDotNet's disassembler doesn't run on macOS, so CI passes `--disasm` on Linux instead.
- **The tests found a real bug.** `ValueSpan` kept the previous token's bytes on `[`, `{`, `]` and `}` tokens.
