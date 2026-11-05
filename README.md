# .NET Infrastructure Ladder

Ten pieces of infrastructure, each rebuilt in C# on .NET 10 from its spec rather than a tutorial. Three are done and two are in progress. Each one is judged by an external test suite or a reference implementation.

Live progress: https://dotnet-ladder.vercel.app

| # | Project | Spec | External judge | Status |
|---|---|---|---|---|
| 01 | [JSON parser](01-json/) | RFC 8259 | JSONTestSuite + System.Text.Json | Done |
| 02 | [Redis-compatible server](02-resp/) | RESP2 | Valkey test suite (251/260 on CI), `valkey-cli`, `valkey-benchmark` | Done (Valkey 9.1.2 reruns pending) |
| 03 | HTTP/1.1 server | RFC 9112 | curl + RFC edge cases; load test vs Kestrel | In progress (nothing published yet) |
| 04 | WebSocket server | RFC 6455 | Autobahn TestSuite | Planned |
| 05 | [DI container](05-di/) | M.E.DependencyInjection | `Specification.Tests` 143/143 + differential vs Microsoft's container | Done |
| 06 | Source-generated mapper | Roslyn | Verify snapshots + Native AOT | Planned |
| 07 | Crash-safe KV store | WAL / log-structured | Power-loss simulation (LazyFS, dropping unsynced writes) + kill -9 harness | Planned |
| 08 | Lox interpreter | Crafting Interpreters | Its test suite | In progress (nothing published yet) |
| 09 | SQL engine | SQL subset | sqllogictest vs sqlite3 | Planned |
| 10 | Raft | Raft paper | Jepsen Maelstrom | Planned |

Each project writes its claims down before the code that tests them (01's first three claims were committed together with its first code, so git can't show which came first), then ships:
- the external judge's raw output;
- BenchmarkDotNet tables with allocation;
- where a claim needs one, a `dotnet-counters` or `dotnet-trace` capture with an explanation;
- a Stryker.NET mutation score;
- a README covering the decision, the claims, the evidence and what surprised us.

Claims that fail stay in the README as failed.

07's judge needs more than kill -9: a killed process loses nothing the kernel already holds, because the OS page cache survives it and still reaches disk, so only a simulated power loss that discards unsynced writes can show whether the store calls `fsync` where it must.

## Run it

```sh
dotnet tool restore
dotnet test --solution Ladder.slnx
01-json/judge/run-judge.sh
tools/ci-local.sh --deep   # adds benchmarks and Stryker
```

Requires the .NET 10 SDK (see `global.json`). External data is listed and cited in [DATA-SOURCES.md](DATA-SOURCES.md).
