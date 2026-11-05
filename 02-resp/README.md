# 02 · Redis-compatible server on System.IO.Pipelines

**Concept:** System.IO.Pipelines. That means pumping a socket into a `Pipe`, parsing commands that arrive split across buffer segments (`ReadOnlySequence<byte>`), back-pressure, and answering pipelined requests in one write.
**Spec:** RESP2, as documented at https://valkey.io/topics/protocol/. Valkey is the BSD-licensed fork of Redis and its wire protocol is identical. Command behaviour follows https://valkey.io/commands/, the JSON command specs in `valkey/src/commands/`, and the C sources (`t_string.c`, `db.c`, `expire.c`), all at tag 9.1.2.
**Source read first:** `dotnet/runtime` `System.IO.Pipelines/Pipe.cs`, for how `AdvanceTo(consumed, examined)` drives back-pressure. `dotnet/aspnetcore` `Kestrel/Transport.Sockets/.../SocketConnection.cs`, whose receive loop does a zero-byte receive before renting a buffer; this server copies that.

## Claims (committed in `05c1fed`, before any server code existed)

| # | Claim | Result |
|---|---|---|
| 1 | Of the **259** tests real Valkey 9.1.2 passes in `unit/type/incr`, `unit/type/string`, `unit/keyspace`, `unit/expire` (suite tags `needs:debug`, `needs:repl`, `needs:reset` excluded), ours passes **≥ 90%**, and **100% of `unit/type/incr`** | **Held.** On CI at `058a8e5` (Valkey 9.1.2 built from source): 251/260 (96.5%), incr 31/31. The 260th test, `trim on SET with big value`, exists only on jemalloc builds, so the suite doesn't run it against ours. On macOS at `348cc76` (Homebrew Valkey, libc malloc, 259 tests): 251/259 (96.9%) |
| 2 | `GET` on an existing key and `INCR` on an existing integer, including parse and reply, allocate **0 B per command** | **Held.** 0 B for 16 pipelined `GET`s and 16 `INCR`s, re-run at `058a8e5` ([`evidence/bdn-058a8e5/`](evidence/bdn-058a8e5/)) |
| 3 | `valkey-benchmark -t set,get -n 1000000 -c 50 -P 16` reaches **≥ 50%** of valkey-server's requests/sec | **Held.** SET 94.5%, GET 81.6% (medians of 3 alternating rounds). **But it uses about 4× the CPU.** See below. Measured at `348cc76`, before the parser fix `91f4b20`; not re-run. |
| 4 | After loading GeoNames `cities15000` into both servers with `valkey-cli --pipe`, full dumps are **byte-for-byte identical** | **Held.** 34,440 keys, identical (CI at `058a8e5`). GeoNames is downloaded fresh each run, so the count drifts: 34,432 on macOS at `348cc76` |

### Latency under fixed-rate load, and a real workload (committed before the load generator and the trace mapper were written; measured at `31d5411` and `5a5ed15`, before the parser fix `91f4b20`)

Closed-loop tools like `valkey-benchmark` send the next request only when a reply arrives, so a slow server is offered less load and its queueing delay never shows up. These claims use an open-loop generator: requests are scheduled at a fixed rate, and each one's latency is measured from when it was *scheduled*, not when it was sent.

| # | Claim | Result |
|---|---|---|
| 5 | At fixed rates of 25%, 50% and 75% of Valkey's closed-loop `GET` maximum on the same machine (50 connections, no pipelining), ours' **p99 latency is within 2×** of Valkey's and its **p99.9 within 3×** | **Pending for Valkey 9.1.2** (it could not be built in the session). Against the Valkey 7.2.13 stand-in: holds, p99 1.00–1.67×, p99.9 0.44–1.04×. VM stalls of 20–30 ms dominate the tails. See [`evidence/openloop.md`](evidence/openloop.md) |
| 6 | At each of those rates ours uses **≤ 4× Valkey's CPU per request** (the closed-loop ratio in claim 3). Prediction, not a goal: the ratio is *worse* at the lowest rate, because waking three async loops per request costs the same however idle the server is | **Pending for 9.1.2.** Against 7.2.13: holds, 3.34× / 2.39× / 1.91× at 25/50/75%. The prediction held too: the ratio is worst at the lowest rate |
| 7 | Prediction: Garnet (Microsoft Research's C# RESP server) uses **less CPU per request than ours** at every rate, so the gap to Valkey is not simply "C# versus C" | **Failed** (Garnet 2.2.5 on the same VM). Garnet and ours are within 6% of each other at every rate: 78 vs 83, 50 vs 49 and 33 vs 32 µs/request. Garnet is cheaper only at 25% |
| 8 | Replaying a real production cache trace (Twitter, Yang et al., OSDI 2020) through ours and Valkey, with trace operations mapped to RESP commands, **every reply is byte-for-byte identical** | **Pending for 9.1.2.** Against the Valkey 7.2.13 stand-in: holds, 1,800,000 replies (two samples, 2020Mar/cluster022 and cluster014), 0 mismatches. No error replies occurred, so error texts are not tested by this. See [`evidence/trace-replay/summary.md`](evidence/trace-replay/summary.md) |

### Coverage-guided fuzzing (committed before the fuzz harness was written)

| # | Claim | Result |
|---|---|---|
| 9 | SharpFuzz + AFL++ on `RespParser`, seeded with captured RESP traffic (the Valkey test suite's requests and the GeoNames load), for **≥ 4 CPU-hours**: no exception escapes, and every input parses to the same sequence of requests, statuses and errors whether it arrives in one segment, one byte per segment, or split at fuzzer-chosen points. Every crash becomes a regression test and a fix | **Pending for the current parser:** 1.0 of the 4 CPU-hours so far (16.8M inputs, 0 crashes, 0 hangs) and a replay of the old corpus; its coverage report wasn't kept. **Held for the parser before `91f4b20`**, with a change of seeds: the Valkey suite's requests were not captured, so the open-loop generator's traffic and two Twitter trace samples were used alongside GeoNames. 4.08 CPU-hours, 86M inputs, 0 crashes, 0 hangs, and the final corpus covers 100% of that parser's lines and branches. The parser was then fixed to match Valkey 9.1.2 on malformed lengths. See [`evidence/fuzzing.md`](evidence/fuzzing.md) |

## Decision

- **Three loops per connection, two pipes** (Kestrel's shape): receive, process and send.
- **Back-pressure on output.** The process loop stops a pipelined batch once 64 KB of replies are pending, flushes, and resumes from the first request it hasn't run. The flush waits whenever the client isn't reading (the output pipe pauses at 1 MB). A client that pipelines thousands of large `GET`s without reading therefore holds about 1 MB of server memory, not the whole reply.
- **No back-pressure on a single incomplete request, by design of `Pipe`.** A `Pipe` counts *unexamined* bytes, and the parser examines a partial request completely. Instead, a request over 1 GB closes the connection, as Valkey's `client-query-buffer-limit` does.
- **The parser never copies an argument that sits in one segment.** `CommandArgs` holds `ReadOnlyMemory<byte>` slices of the pipe's buffer, and copies into a per-connection arena only when an argument straddles two segments. A request that is not complete yet is left unconsumed and re-parsed when more bytes arrive.
- **Keys are probed with spans** through `Dictionary.GetAlternateLookup<ReadOnlySpan<byte>>`, so a lookup straight from the input buffer allocates no key.
- **Integers are stored as `long`** (Valkey's `int` encoding), so `INCR` updates in place.
- **One lock around command execution** reproduces Valkey's one-command-at-a-time semantics; parsing and I/O run outside it.
- **Expiry** is lazy (checked on access) plus an active cycle every 100 ms that samples 20 volatile keys and repeats while more than 25% were expired, which is Valkey's algorithm.
- **SCAN's cursor is a position in stable-hash order**, so a key present for the whole iteration is returned even while other keys are added or removed (a property test checks this).

## Evidence

- **Judge:** [`judge/results/summary.txt`](judge/results/summary.txt) (per-test verdicts, CI at `058a8e5`), with raw runs in `judge/results/suite-reference.txt` and `suite-ours.txt`. The earlier macOS run is in [`judge/results/macos-348cc76/`](judge/results/macos-348cc76/). `judge/run-judge.sh` downloads Valkey's suite at the pinned commit and runs it in external-server mode with `--durable` against both servers. With `--durable`, an unsupported command fails only its own test instead of aborting the rest of the file.
- **The 8 failures**, all features not built: keyspace notifications over pub/sub (1), streams (2), Valkey's import mode (4), and `MOVE basic usage`, which fails only because the failed stream tests left keys behind in db 9 (1). On Linux one more test is not run against ours: it needs a jemalloc build.
- **Allocation and throughput:** [`evidence/benchmarks.md`](evidence/benchmarks.md) and [`evidence/throughput.md`](evidence/throughput.md).
- **CPU profile and experiment:** [`evidence/throughput.md`](evidence/throughput.md#where-the-cpu-goes).
- **Tests:** 103 xUnit tests, including CsCheck properties:
  - any request split across arbitrary buffer segments parses the same as in one piece;
  - SCAN returns every stable key under concurrent churn.
  Socket tests cover byte-at-a-time pipelining, protocol errors, a 4 MB request, and output back-pressure under 2,000 unread replies.
- **Open-loop comparison with Valkey 7.2.13 (stand-in) and Garnet 2.2.5:** [`evidence/openloop.md`](evidence/openloop.md).
- **Twitter cache-trace replay:** [`evidence/trace-replay/summary.md`](evidence/trace-replay/summary.md). Rerun with `judge/run-trace-replay.sh`.
- **Fuzzing:** [`evidence/fuzzing.md`](evidence/fuzzing.md).
- **Mutation testing** (before the parser fix `91f4b20`; not re-run): Stryker scored **51.37%**. Re-testing every survivor in a fresh process (52 more killed) and against the Valkey suite (410 more) gives **72.2%**. 649 mutants survive everything, and each is classified: 429 are on lines no unit test runs, 157 are test gaps, 35 are equivalent and 28 change only performance. Most are in commands that exist only to support the suite (`CLIENT`, `HELLO`, `CONFIG`, `OBJECT`, `SCAN`). Closing the parser's gaps found and fixed five parser bugs on malformed input. See [`evidence/stryker.md`](evidence/stryker.md).

## What surprised us

- **The fully fuzzed parser was wrong in five places.** The fuzz corpus executes every line and branch of `RespParser`. Yet mutation testing showed that nothing would notice if the parser started accepting a length with no digits (`*\r\n`) or a 19-digit length. The fuzzer's oracle checks that splitting the input doesn't change the parse; it cannot check that the parse is right. We closed the gap with tests taken from Valkey 9.1.2's `networking.c`, and the existing parser failed them in five places. It accepted leading zeros and `-0`, gave the wrong error after a bad `\r`, and refused `*1\n` where Valkey waits for more input. Two of our own earlier tests had encoded the wrong answers. The Valkey 7.2.13 stand-in would not have caught the CRLF cases: it skips whatever byte follows `\r`. Details: [`evidence/stryker.md`](evidence/stryker.md).
- **Garnet costs as much CPU per request as ours.** We predicted that Microsoft Research's server would be clearly cheaper (claim 7). Under the same open-loop load, the two are within 6% of each other at every rate, and both cost 1.9–3.3× what Valkey does. Whatever drives this cost, both C# servers pay it; Garnet's engineering does not avoid it.
- **The judge found two real bugs before it found any missing features.**
  - A `KEYS` pattern of 50,000 `*?` pairs made the glob matcher backtrack exponentially while holding the keyspace lock. That froze the whole server, so every later test timed out (`unit/expire` scored 0/65 on that run; run not kept). Porting Valkey's "skip longer matches" cut-off fixed it.
  - `SETRANGE key 9223372036854775807 A` overflowed `offset + length`, threw, and dropped the client. Command handlers now catch unexpected exceptions and reply with an error instead.
- **A security review found what the judge couldn't.** The first version ran every complete request in the input buffer before flushing, so pipelined `GET`s of a 64 KB value made the server allocate **139.7 MB** of replies the client never read (measurement not kept). A socket test now reproduces it and checks every reply still arrives, in order, once the client reads. Looking into it also disproved a bug I suspected: a 4 MB `SET` does not deadlock against the input pipe's 1 MB pause, because `Pipe.AdvanceTo` releases the writer on *examined* bytes, not consumed ones.
- **One unsupported command can hide 58 passing tests.** Without `--durable`, the first `PSUBSCRIBE` in `string.tcl` aborted the rest of the file, and the score read 68.7% (run not kept). The flag is upstream and applies to both servers equally.
- **Requests per second was the wrong headline.** Ours reaches 82–95% of Valkey's throughput while using about 4× its CPU (3.2 µs vs 0.8 µs per request; measured at `348cc76`). Command execution is only 1% of the samples; thread-pool spinning (15%) and lock spinning (6%) are larger (trace not kept). Turning the spinning off saves 16–21% of the CPU (best run vs best run, median vs median; [`evidence/cpu-experiment.csv`](evidence/cpu-experiment.csv)); the rest is the cost of three async loops and thread hops per connection, against Valkey's single event loop.
- **`INCRBYFLOAT 10.5` then `0.1` returns `10.59999999999999964`, and that's correct.** Valkey formats with `%.17Lf`, and on Apple Silicon `long double` is a plain `double`. Real Valkey 9.1.2 on the same Mac gives the same bytes ([`evidence/incrbyfloat-mac.txt`](evidence/incrbyfloat-mac.txt)).
