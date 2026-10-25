# .NET Infrastructure Ladder

Ten pieces of infrastructure rebuilt in C# on .NET 10, each from its spec rather than a tutorial. Each one is judged by an external test suite or a reference implementation, not by its author.

Live progress: https://dotnet-ladder.vercel.app

| # | Project | Spec | External judge | Status |
|---|---|---|---|---|
| 01 | [JSON parser](01-json/) | RFC 8259 | JSONTestSuite + System.Text.Json | Done |
| 02 | Redis-compatible server | RESP | Valkey test suite, `valkey-cli`, `valkey-benchmark` | In progress |
| 03 | HTTP/1.1 server | RFC 9112 | curl + RFC edge cases; load test vs Kestrel | Planned |
| 04 | WebSocket server | RFC 6455 | Autobahn TestSuite | Planned |
| 05 | DI container | M.E.DependencyInjection | `Specification.Tests` | Planned |
| 06 | Source-generated mapper | Roslyn | Verify snapshots + Native AOT | Planned |
| 07 | Crash-safe KV store | WAL / log-structured | kill -9 harness | Planned |
| 08 | Lox interpreter | Crafting Interpreters | Its test suite | Planned |
| 09 | SQL engine | SQL subset | sqllogictest vs sqlite3 | Planned |
| 10 | Raft | Raft paper | Jepsen Maelstrom | Planned |

Each project writes its claims down before any code, then ships:
- the external judge's raw output;
- BenchmarkDotNet tables with allocation;
- a `dotnet-counters` or `dotnet-trace` capture with an explanation;
- a Stryker.NET mutation score;
- a one-page README covering the decision, the claims, the evidence and what surprised us.

Claims that fail stay in the README as failed.

## Run it

```sh
dotnet tool restore
dotnet test --solution Ladder.slnx
01-json/judge/run-judge.sh
tools/ci-local.sh --deep   # adds benchmarks and Stryker
```

Requires the .NET 10 SDK (see `global.json`). External data is listed and cited in [DATA-SOURCES.md](DATA-SOURCES.md).
