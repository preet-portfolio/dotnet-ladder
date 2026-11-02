# Mutation testing

**Result:** Stryker reports **51.37%**. Counting what the external judge kills, the score is **72.2%** (1,683 / 2,332).
**649 mutants survive everything**, and each one is classified below. Most of them are commands that no test sends:
- **429** are on lines no unit test executes. The Valkey suite files used as the judge don't detect them either.
- **220** are executed by unit tests without being detected:
  - **157 are test gaps**, 33 of them off-by-one edges at a limit;
  - 35 are equivalent (23 [Likely]);
  - 28 change only performance (8 [Likely]).

The gaps that matter most were in the RESP parser. Breaking its length checks went unnoticed by the unit tests, the
judge and 86 million fuzz inputs. Closing those gaps against Valkey 9.1.2's source then found **five places where the
parser was wrong**, all on malformed input. They are now fixed and tested.

## How it was run

```sh
cd 02-resp
dotnet dotnet-stryker -f stryker-config.json --concurrency 2    # coverage-analysis: perTest
```

- **Tools:** Stryker 5.0.0 with the `mtp` runner, on the shared cloud VM: Intel Xeon Processor @ 2.10GHz, 4 vCPUs.
  Stryker was pinned to 2 of them, and the AFL++ runs had the other two. Stryker took 30 min 43 s.
- **Coverage analysis:** the earlier setting, `coverage-analysis: off`, runs all 81 tests for every mutant. That run
  did not finish in 2 hours. `perTest` runs only the tests that cover each mutant, and it finished in 31 minutes.
- **Judge re-test:** every mutant Stryker left alive, 371 Survived and 785 NoCoverage, was re-tested by
  [`tools/stryker/judge_survivors.py`](../../tools/stryker/judge_survivors.py). It applies one mutant to the source at a
  time, runs [`tools/stryker/check-02-mutant.sh`](../../tools/stryker/check-02-mutant.sh), and restores the file. That
  script does three things:
  1. builds the server and the tests;
  2. runs the unit tests in a **fresh process**; this is skipped for NoCoverage mutants, which no unit test reaches;
  3. runs the external judge: Valkey's own Tcl suite files `unit/type/incr`, `unit/type/string`, `unit/keyspace` and
     `unit/expire`, with the same tags as [`judge/run-judge.sh`](../judge/run-judge.sh). The suite is at commit
     `7f1dffed`, and it runs against the mutated server.

  A mutant is killed if the unit tests fail, or if the suite fails a test that the unmutated server passes.
- **Baseline:** the unmutated server passes 248 suite tests in this setup. The run used three baselines, all
  identical. That is 3 fewer than claim 1's 251; the helper binaries here are the Valkey 7.2.13 stand-in, not 9.1.2. The
  comparison is test by test against these 248, so a test that already fails cannot count as a kill.
- **Parallelism:** 4 judges ran side by side, each in its own git worktree, on its own port and CPU. It took about 2 h
  30 min for 1,156 mutants.
- **Is the judge stable under load?** We checked this two ways:
  - 53 mutants were judged twice, because two workers overlapped, and every pair agreed.
  - The 187 kills that rest on only 1 or 2 lost suite tests are being judged again with 3 workers instead of 4.
    Several of the lost tests sleep for EXPIRE, so they are the likeliest to be flaky. **Pending:** so far every
    re-judged mutant has been killed again.
- **Raw data:**
  - [`stryker/stryker-report.md`](stryker/stryker-report.md): Stryker's own table.
  - [`stryker/judged-mutants.csv`](stryker/judged-mutants.csv): every one of the 1,156 mutants, with its verdict and
    the first lost suite test.
  - [`stryker/survivors-classified.csv`](stryker/survivors-classified.csv): the 649 survivors with their class and the
    reason.
  - [`stryker/classify.py`](stryker/classify.py): the classification. A comment states the reason for each mutant id.

## Score

| | Mutants |
|---|---:|
| Killed by Stryker (incl. 33 timeouts) | 1,221 |
| Survived (Stryker) | 371 |
| No coverage (Stryker) | 785 |
| … of the 1,156: killed by the fresh-process unit tests | 52 |
| … of the 1,156: killed by the Valkey suite | 410 |
| … of the 1,156: do not compile when applied to the file by hand | 45 |
| **Survive everything** | **649** |
| Compile errors / ignored / runtime error (Stryker) | 387 / 490 / 1 |

Adjusted score = (1,221 + 52 + 410) / (2,377 − 45) = 1,683 / 2,332 = **72.2%**. Stryker's own figure is 51.37%.

## Where the survivors are

These are the methods with the most survivors. The method names come from a regex over the source
([`group_by_member.py`](../../tools/stryker/group_by_member.py)), so a lambda inside `Register` counts as `Register`.

| Method | Survivors | Untested | Test gap | Equivalent | Perf only |
|---|---:|---:|---:|---:|---:|
| `KeyCommands.Scan` (SCAN) | 51 | 51 | | | |
| `TypeCommands.Register` (list/set/hash/zset table and one-line commands) | 36 | 18 | 18 | | |
| `ServerCommands.ClientCommand` (CLIENT) | 34 | 34 | | | |
| `ServerCommands.Config` (CONFIG defaults) | 29 | | 29 | | |
| `ServerCommands.Hello` (HELLO) | 28 | 28 | | | |
| `KeyCommands.EncodingName` (OBJECT ENCODING) | 27 | 26 | 1 | | |
| `StringCommands.Lcs` (LCS) | 23 | 16 | 7 | | |
| `GlobMatcher.Match` | 19 | 8 | 10 | | 1 |
| `ServerCommands.Register` | 19 | 1 | 18 | | |
| `KeyCommands.ObjectCommand` (OBJECT) | 17 | 17 | | | |
| Other 89 methods | 366 | 230 | 74 | 35 | 27 |

The pattern: the judge is four suite files about strings, integers, keys and expiry. The unit tests cover what was
built against those files. Everything else exists only so that the suite files' helper calls work: `CLIENT`,
`HELLO`, `CONFIG`, `OBJECT`, `SCAN`, the collection types and `INFO`. It is hardly tested at all.

## The parser gaps that matter, and what closing them found

- **`RespParser.cs:171`, mutant 2584 (`||` → `&&`).** The mutant accepts `*\r\n` (a length with no digits) and any
  byte other than `\n` after `\r`. Nothing would notice if the check broke:
  - no unit test sends a length with no digits;
  - the suite files don't send one either;
  - the fuzzer cannot tell. Its oracle compares the parse across different splits of the same bytes, and a parser that
    accepts too much is consistent across splits. The fuzzer covered the line without being able to judge it.
- **`RespParser.cs:186/192`, mutants 2607 and 2613.** They weaken or remove the 18-digit cap on lengths, so a 19-digit
  length overflows `long` instead of being refused.
- **Mutants 2514, 2537 and 2555** move the parser's limits (1M arguments, 512 MB bulk, 64 KB inline) by one.

To close these gaps, we wrote tests whose expected replies come from Valkey 9.1.2's `processMultibulkBuffer`
(`src/networking.c` at the judge's commit `7f1dffed`). Writing them showed that the **unmutated parser was wrong in
five places**:

| Input | Valkey 9.1.2 (source) | Ours, before |
|---|---|---|
| `*1\rX` (byte after `\r` is not `\n`) | `invalid CRLF in request` | `invalid multibulk length` |
| `*1\r\n$3\r\nGETxx` (payload not followed by `\r\n`) | `invalid CRLF in request` | `invalid bulk length` |
| `*01\r\n`, `$04` (leading zero) | `invalid multibulk length` / `invalid bulk length` (`string2ll`) | accepted as 1 / 4 |
| `*-0\r\n` | `invalid multibulk length` | accepted as an empty request |
| `*1\n` (no `\r` yet) | waits for more input | `invalid multibulk length` |

Also, with no `\r` within 64 KB, Valkey replies `too big mbulk count string` or `too big bulk count string`; ours had no
such check.

The Valkey 7.2.13 stand-in agrees with ours-before on the CRLF cases. It skips whatever byte follows `\r`:
`*1\rX$4\r\nPING\r\n` gets `+PONG`. So the stand-in could not have shown the difference.

`TryReadLengthLine` now follows Valkey's order of checks:
1. find `\r`;
2. wait for the byte after it;
3. check the marker;
4. check that the byte is `\n`;
5. parse the number with `string2ll`'s rules.

There are 21 new parser test cases (`ParserTests`), plus a test that parsing 16 pipelined requests, whole or split
into 1-byte segments, allocates 0 B. Claim 2 counts the parse, but its existing test called the command table
directly. The suite has 103 tests, all passing. We re-checked after
the fix:
- **The fuzz corpus** (617 inputs) still passes the split oracle.
- **The suite baseline** is still the same 248 tests.
- **A 1-hour fuzz run** of the new parser is reported in [`fuzzing.md`](fuzzing.md).

These tests come after this Stryker run, so they are not in the score above.

## Classes

- **Untested (429):** Stryker found no unit test that executes the line, and the judge doesn't detect the change.
  These were not examined one by one for equivalence. By construction they are gaps in testing, unless some are
  equivalent.
- **Test gap (157):** the line runs in a unit test and the behaviour can differ, but nothing checks it. 33 are
  **edges**: off by one at a limit (1 GB query buffer, 512 MB bulk, 1M arguments, int ranges, expiry exactly at `now`)
  or an option given twice.
- **Equivalent (35, 23 [Likely]):** no client can observe a difference. The largest group is 10 `>= 0` → `> 0` mutants
  on expiry times. An expiry at Unix time 0 cannot be stored, because an EXPIREAT in the past deletes the key.
  [Likely], because that is argued from the code, not tested.
- **Performance only (28, 8 [Likely]):** buffer growth, pipe thresholds, the cached SCAN order, and active-expiry
  sampling. The [Likely] ones are active-expiry tuning: it changes how long expired keys hold memory, but lazy expiry
  still hides them from every command.

Each surviving mutant's class and reason are in [`stryker/survivors-classified.csv`](stryker/survivors-classified.csv).

## What surprised us

- **Stryker's 2-hour run did nothing visible.** With coverage analysis off, it ran all 81 tests per mutant, including
  the socket tests, and logged no progress until it was killed at the time limit. Per-test coverage finished in 31
  minutes.
- **The static command table hid 51 kills from Stryker.** The command table is built in a static constructor, once
  per test process. Stryker's `mtp` runner reuses that process, so mutants in the `Register` methods were never
  active. Stryker reported them as Survived. Run in a fresh process, the same unit tests kill 51 of them. 05 had the
  same effect with a static dictionary initializer.
- **Coverage is not detection, even for the fuzzed parser.** The final fuzz corpus executes every line and branch of
  `RespParser`, yet 8 parser mutants survive: 3 gaps, 3 edges, 1 equivalent, and 1 on a line only the fuzzer reaches. The fuzzer's oracle checks that splitting the input
  doesn't change the parse; it cannot check that the parse is *right*.
- **The judge kills only what it reaches.** The four suite files killed 410 of the 1,156 mutants. They could not reach
  the code that supports them (`CLIENT`, `HELLO`, `CONFIG`), so the 251/259 of claim 1 says nothing about that code.

## Not done

- **Only the parser gaps are closed.** SCAN, CLIENT/HELLO/CONFIG, OBJECT ENCODING and the other untested commands
  still need tests compared with Valkey's replies. Re-running Stryker and the judge (about 3 hours on this VM) would
  then show what the new tests kill.
- **The judge used the 7.2.13 stand-in's helper binaries** and the suite at `7f1dffed`. Valkey 9.1.2 could not be
  built in this session; see the claims table.
