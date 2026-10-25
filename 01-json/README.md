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

## Claims (written before the code)

| # | Claim | Result |
|---|---|---|
| 1 | Tokenizing an in-memory document allocates 0 bytes | **Held.** BenchmarkDotNet `Allocated` = 0 B on all three files; `Tokenizing_allocates_nothing` unit test |
| 2 | JSONTestSuite: 100% of `y_` accepted, 100% of `n_` rejected, no crash | **Held.** 95/95 and 188/188, including `n_structure_100000_opening_arrays` |
| 3 | Throughput within 2× of `Utf8JsonReader` | **Held, with margin.** 0.51–0.64× of its time (ours is faster) |

## Evidence

- External judge: [`judge/results/jsontestsuite.txt`](judge/results/jsontestsuite.txt) (one line per file, ours vs System.Text.Json). Rerun with `judge/run-judge.sh`, which downloads JSONTestSuite @ `1ef36fa` and the corpus @ `478d572`.
- Comparison with System.Text.Json: 0 tree mismatches on the 95 files both accept. Every disagreement and every `i_` result is in [`evidence/divergences.md`](evidence/divergences.md).
- Benchmarks (Apple M1, .NET 10.0.13): [`evidence/benchmarks.md`](evidence/benchmarks.md).

  | File | Ours | Utf8JsonReader | Ratio | Allocated |
  |---|---:|---:|---:|---:|
  | canada.json (2.2 MB, numbers) | 1,938 µs | 3,782 µs | 0.51 | 0 B |
  | citm_catalog.json (1.7 MB) | 1,615 µs | 2,983 µs | 0.54 | 0 B |
  | twitter.json (0.6 MB, non-ASCII) | 526 µs | 822 µs | 0.64 | 0 B |

- Runtime capture: [`evidence/counters.md`](evidence/counters.md). 0 GCs and 0 pause time during a 30 s loop. The explanation of the non-zero allocation rate dotnet-counters reported is there too.
- Tests: 101 xUnit tests. They include two CsCheck properties: 20,000 documents written by `Utf8JsonWriter` must parse back to the same values, and 50,000 randomly mutated documents must never throw and must agree with `Utf8JsonReader` on validity whenever the input is valid UTF-8.
- Mutation testing: **97.83%** (313 killed, 3 timeouts, 7 survivors, all equivalent). See [`evidence/stryker.md`](evidence/stryker.md).

## What surprised us

- **System.Text.Json accepts invalid UTF-8 at parse time.** On 20 of the 35 `i_` files, `JsonDocument.Parse` succeeds and the error only appears later, as an `InvalidOperationException` when a string is read. `Utf8JsonReader` checks strings only for quotes, backslashes and control bytes. Ours rejects these files when it reads them.
- **UTF-8 validation is nearly free.** `Utf8.IsValid` over a whole file takes 40–64 µs, 2–7% of a tokenize pass. We expected the validation to be the reason we'd lose to STJ. It wasn't.
- **We're faster than `Utf8JsonReader`, and the cause hasn't been measured.** The likely reason is that STJ does work we don't: line and position tracking, support for streaming partial buffers, and comment and trailing-comma options. That is a hypothesis; no profile has confirmed it yet.
- **STJ's timing on canada.json moved up to 20% between runs** (3,267, 3,975 and 3,782 µs in three processes), while ours stayed within 1,904–1,938 µs. All runs are kept in the evidence.
- **dotnet-counters reported allocation that wasn't ours.** It showed 16–49 KB/s across two captures, which turned out to come from the in-process metrics listener it enables. A process-wide counter can't prove a per-call zero; BenchmarkDotNet and a unit test can.
- **The tooling needed checking too.** Stryker's first run reported a 0% score because it used the old VSTest runner while the repo runs on Microsoft.Testing.Platform. BenchmarkDotNet's disassembler doesn't run on macOS, so CI passes `--disasm` on Linux instead.
- **The tests found a real bug.** `ValueSpan` kept the previous token's bytes on `[`, `{`, `]` and `}` tokens.
