# 02 · Redis-compatible server on System.IO.Pipelines

**Concept:** System.IO.Pipelines. That means pumping a socket into a `Pipe`, parsing commands that arrive split across buffer segments (`ReadOnlySequence<byte>`), back-pressure, and answering pipelined requests in one write.
**Spec:** RESP2, as documented at https://valkey.io/topics/protocol/. Valkey is the BSD-licensed fork of Redis and its wire protocol is identical. Command behaviour follows https://valkey.io/commands/, the JSON command specs in `valkey/src/commands/`, and the C sources (`t_string.c`, `db.c`, `expire.c`), all at tag 9.1.2.
**Source read first:** `dotnet/runtime` `System.IO.Pipelines/Pipe.cs`, for how `AdvanceTo(consumed, examined)` drives back-pressure. `dotnet/aspnetcore` `Kestrel/Transport.Sockets/.../SocketConnection.cs`, whose receive loop does a zero-byte receive before renting a buffer; this server copies that.

## Claims (committed in `05c1fed`, before any server code existed)

| # | Claim | Result |
|---|---|---|
| 1 | Of the **259** tests real Valkey 9.1.2 passes in `unit/type/incr`, `unit/type/string`, `unit/keyspace`, `unit/expire` (suite tags `needs:debug`, `needs:repl`, `needs:reset` excluded), ours passes **≥ 90%**, and **100% of `unit/type/incr`** | **Held.** 251/259 (96.9%); incr 31/31 |
| 2 | `GET` on an existing key and `INCR` on an existing integer, including parse and reply, allocate **0 B per command** | **Held.** 0 B for 16 pipelined `GET`s and 16 `INCR`s |
| 3 | `valkey-benchmark -t set,get -n 1000000 -c 50 -P 16` reaches **≥ 50%** of valkey-server's requests/sec | **Held.** SET 91.7%, GET 82.1% (medians of 3 alternating rounds). **But it uses about 4× the CPU.** See below. |
| 4 | After loading GeoNames `cities15000` into both servers with `valkey-cli --pipe`, full dumps are **byte-for-byte identical** | **Held.** 34,432 keys, identical |

## Decision

- **Three loops per connection, two pipes** (Kestrel's shape): receive, process and send. The pipe pause thresholds (1 MB) are the back-pressure: a client that floods requests, or reads replies slowly, stalls only its own loops.
- **The parser never copies an argument that sits in one segment.** `CommandArgs` holds `ReadOnlyMemory<byte>` slices of the pipe's buffer, and copies into a per-connection arena only when an argument straddles two segments. A request that is not complete yet is left unconsumed and re-parsed when more bytes arrive.
- **Keys are probed with spans** through `Dictionary.GetAlternateLookup<ReadOnlySpan<byte>>`, so a lookup straight from the input buffer allocates no key.
- **Integers are stored as `long`** (Valkey's `int` encoding), so `INCR` updates in place.
- **One lock around command execution** reproduces Valkey's one-command-at-a-time semantics; parsing and I/O run outside it.
- **Expiry** is lazy (checked on access) plus an active cycle every 100 ms that samples 20 volatile keys and repeats while more than 25% were expired, which is Valkey's algorithm.
- **SCAN's cursor is a position in stable-hash order**, so a key present for the whole iteration is returned even while other keys are added or removed (a property test checks this).

## Evidence

- **Judge:** [`judge/results/summary.txt`](judge/results/summary.txt) (per-test verdicts), with raw runs in `judge/results/suite-reference.txt` and `suite-ours.txt`. `judge/run-judge.sh` downloads Valkey's suite at the pinned commit and runs it in external-server mode with `--durable` against both servers. With `--durable`, an unsupported command fails only its own test instead of aborting the rest of the file.
- **The 8 failures**, all features not built: keyspace notifications over pub/sub (1), streams (2), Valkey's import mode (4), and `MOVE basic usage`, which fails only because the failed stream tests left keys behind in db 9 (1).
- **Allocation and throughput:** [`evidence/benchmarks.md`](evidence/benchmarks.md) and [`evidence/throughput.md`](evidence/throughput.md).
- **CPU profile and experiment:** [`evidence/throughput.md`](evidence/throughput.md#where-the-cpu-goes).
- **Tests:** 79 xUnit tests, including CsCheck properties:
  - any request split across arbitrary buffer segments parses the same as in one piece;
  - SCAN returns every stable key under concurrent churn.
  Socket tests cover byte-at-a-time pipelining, protocol errors and 100 KB values.
- **Mutation testing:** [`evidence/stryker.md`](evidence/stryker.md).

## What surprised us

- **The judge found two real bugs before it found any missing features.**
  - A `KEYS` pattern of 50,000 `*?` pairs made the glob matcher backtrack exponentially while holding the keyspace lock. That froze the whole server, so every later test timed out (`unit/expire` scored 0/65 on that run). Porting Valkey's "skip longer matches" cut-off fixed it.
  - `SETRANGE key 9223372036854775807 A` overflowed `offset + length`, threw, and dropped the client. Command handlers now catch unexpected exceptions and reply with an error instead.
- **One unsupported command can hide 58 passing tests.** Without `--durable`, the first `PSUBSCRIBE` in `string.tcl` aborted the rest of the file, and the score read 68.7%. The flag is upstream and applies to both servers equally.
- **Requests per second was the wrong headline.** Ours reaches 82–92% of Valkey's throughput while using about 4× its CPU (3.2 µs vs 0.8 µs per request). Command execution is only 1% of the samples; thread-pool spinning (15%) and lock spinning (6%) are larger. Turning the spinning off saves 15% of the CPU; the rest is the cost of three async loops and thread hops per connection, against Valkey's single event loop.
- **`INCRBYFLOAT 10.5` then `0.1` returns `10.59999999999999964`, and that's correct.** Valkey formats with `%.17Lf`, and on Apple Silicon `long double` is a plain `double`. Real Valkey on the same Mac gives the same bytes.
