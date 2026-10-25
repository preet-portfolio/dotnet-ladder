# 02 · Redis-compatible server on System.IO.Pipelines

**Concept:** System.IO.Pipelines: pumping a socket into a `Pipe` without copying, parsing commands that arrive split across buffer segments (`ReadOnlySequence<byte>`), back-pressure, and replying to pipelined requests in one write.
**Spec:** RESP2, as documented at https://valkey.io/topics/protocol/ (Valkey is the BSD-licensed fork of Redis; the wire protocol is identical). Command semantics follow https://valkey.io/commands/.
**Source to read first:** `dotnet/runtime` `src/libraries/System.IO.Pipelines/src/System/IO/Pipelines/Pipe.cs` (how `AdvanceTo(consumed, examined)` drives back-pressure) and `dotnet/aspnetcore` `src/Servers/Kestrel/Transport.Sockets/src/Internal/SocketConnection.cs` (Kestrel's socket ↔ pipe pumps).

## Claims (written before the code; committed before any server code exists)

| # | Claim | How it is judged |
|---|---|---|
| 1 | Of the **259** tests that real Valkey 9.1.2 passes in `unit/type/incr`, `unit/type/string`, `unit/keyspace` and `unit/expire` (with the suite's own `needs:debug`, `needs:repl`, `needs:reset` tags excluded), ours passes **≥ 90% (≥ 234)**, and **100% of `unit/type/incr`** | Valkey's Tcl test suite in external-server mode, run against both servers, diffed test by test |
| 2 | Parsing and executing `GET` on an existing key and `INCR` on an existing integer, including writing the reply, allocates **0 bytes per command** | BenchmarkDotNet `[MemoryDiagnoser]` on a pipelined batch of 16 commands |
| 3 | `valkey-benchmark -t set,get -n 1000000 -c 50 -P 16`: ours reaches **≥ 50% of valkey-server's requests/sec** on the same Mac in the same session | `valkey-benchmark` CSV output for both servers |
| 4 | After loading the real GeoNames `cities15000` dataset through `valkey-cli --pipe` into both servers (one hash-free `SET` per city plus an `INCR` per-country counter), a full `SCAN` + `GET` dump of ours is **byte-for-byte identical** to Valkey's | Differential dump comparison |

Data sources are cited in [../DATA-SOURCES.md](../DATA-SOURCES.md).
