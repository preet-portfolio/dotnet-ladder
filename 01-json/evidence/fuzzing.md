# Coverage-guided fuzzing (claim 8)

**Result: held.** The run lasted 4.08 CPU-hours: 469 million inputs and 529 full cycles of the queue. It found
**0 crashes and 0 hangs**. No exception escaped `JsonTokenizer`, its accessors or `JsonDocumentParser`. On every input
that was valid UTF-8, our validity verdict agreed with `Utf8JsonReader`'s.

## How it was run

```sh
tools/fuzz/run-afl.sh 01-json/fuzz/Ladder.Json.Fuzz Ladder.Json.dll <seed dir> <out dir> 14700 2
```

- **Harness:** [`fuzz/Ladder.Json.Fuzz/Program.cs`](../fuzz/Ladder.Json.Fuzz/Program.cs). It does four things:
  1. tokenizes the input;
  2. decodes every string and number token with `TryGetString`, `TryGetDouble` and `TryGetInt64`;
  3. parses the input with `JsonDocumentParser`;
  4. throws if the input is valid UTF-8 and our verdict differs from `Utf8JsonReader`'s (`MaxDepth` set to ours).

  An escaped exception is a crash.
- **Seeds:** all 318 files of JSONTestSuite at `1ef36fa`, the same pinned copy the judge downloads.
- **Tools:**
  - AFL++ 4.09c (Ubuntu package), with `-t 10000 -m none -V 14700`;
  - SharpFuzz 2.3.0, which instruments `Ladder.Json.dll` only;
  - .NET 10.0.13, Release build.
- **Machine:** the shared cloud VM, an Intel Xeon Processor @ 2.10GHz with 4 vCPUs. The fuzzer was pinned to one vCPU
  (`-b 2`). The 02 fuzzer had its own vCPU, and Stryker and the judges were limited to the other two. Throughput is
  therefore a lower bound, and no timing claim depends on it.
- **Raw stats:** [`fuzzing/fuzzer_stats.txt`](fuzzing/fuzzer_stats.txt).

| | |
|---|---:|
| Run time | 14,700 s on 1 vCPU = **4.08 CPU-hours** |
| Executions | 468,709,269 (31,885/s) |
| Queue cycles | 529 |
| Corpus at end | 678 inputs (from 318 seeds) |
| Edges found | 435 (stability 97.93%) |
| Last new path | 28 minutes before the end |
| Crashes / hangs | **0 / 0** |

## Coverage of the final corpus

Every queue entry was replayed through the harness's `check` mode under coverlet.console 6.0.4. This used an
uninstrumented Release build, and only `[Ladder.Json]*` was measured. Raw data:
[`fuzzing/corpus-coverage.cobertura.xml`](fuzzing/corpus-coverage.cobertura.xml).

| File | Lines | Branches |
|---|---:|---:|
| `JsonDocumentParser.cs` | 59/59 (100%) | 37/38 (97.4%) |
| `JsonTokenizer.cs` | 188/198 (94.9%) | 153/159 (96.2%) |
| `JsonString.cs` | 48/52 (92.3%) | 38/43 (88.4%) |
| Assembly | 94.01% | 94.26% |

The harness cannot reach the uncovered lines, because it always calls the API correctly. They are of three kinds:
- **Guards for misuse:** accessors called on the wrong token kind, `Read()` after the end, and `CurrentDepth`.
- **`DestinationTooSmall` returns in `JsonString`:** the harness always passes a buffer of `ValueSpan.Length` chars,
  which the API documents as always large enough.
- **The `Expect.PropertyName` case of `Read()`**, at `JsonTokenizer.cs:84`. It is dead code: nothing ever assigns
  `Expect.PropertyName`, because `ReadCommaOrClose` reads the next property name itself.

## What surprised us

- **Measuring the corpus's coverage found dead code.** `Read()` handles an `Expect.PropertyName` state that nothing
  ever enters. Neither the fuzzer nor the tests could tell us that; only the coverage report of the final corpus did.
- **No crash, but no plateau either.** We expected no crashes, because the CsCheck property (50,000 mutated documents)
  and the JSONTestSuite judge already agree with `Utf8JsonReader`. We did not expect the fuzzer still to be finding new
  paths 28 minutes before the end. A longer run could still find something; 4 CPU-hours is the claim's bound, not a
  proof.
