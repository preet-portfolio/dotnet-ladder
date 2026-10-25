# Throughput and CPU: ladder-resp vs valkey-server 9.1.2

Machine: Apple M1, 8 cores, macOS 27.2. Same session, both servers on loopback, `bench/run-throughput.sh`.

## Claim 3: requests per second

`valkey-benchmark -t set,get -n 1000000 -c 50 -P 16 --csv`, after a 200,000-request warm-up. Three rounds alternating between the servers; medians of the three. Raw rows: [throughput-raw.csv](throughput-raw.csv).

| Test | valkey-server | ladder-resp | ladder / valkey | p99 latency valkey | p99 latency ladder |
|---|---:|---:|---:|---:|---:|
| SET | 1,131,222 | 1,037,344 | **0.917** | 0.815 ms | 0.959 ms |
| GET | 1,381,216 | 1,133,787 | **0.821** | 0.679 ms | 0.855 ms |

The claim (≥ 0.50) holds. Run-to-run noise is large for ours: across the separate runs below, SET varied from 0.78 M to 1.75 M req/s. Valkey varied by about 2%.

## The number the claim did not ask for: CPU

Requests per second hides the cost. Valkey executes commands on one thread; ladder-resp spreads I/O and parsing over the thread pool. Server CPU time for 2,000,000 requests (1 M SET + 1 M GET), read from `ps` before and after each run ([throughput-cpu.csv](throughput-cpu.csv)):

| Server | CPU seconds (3 runs) | Wall seconds | CPU per request |
|---|---|---|---:|
| valkey-server | 1.61, 1.60, 1.61 | 1.64 | 0.8 µs |
| ladder-resp | 7.25, 6.46, 6.36 | 1.22–1.49 | 3.2 µs |

**ladder-resp uses about 4× the CPU of Valkey for the same work.** It matches Valkey's throughput only by using about five cores where Valkey uses one.

## Where the CPU goes

`dotnet-trace collect --profile dotnet-sampled-thread-time` during an 8-second benchmark (macOS supports only this sampled thread-time profile, not kernel CPU sampling), aggregated by the frame directly under the sampler's CPU marker:

| Share of samples | What |
|---:|---|
| 15.2% | Thread-pool workers spinning in `LowLevelLifoSemaphore` while waiting for work |
| 5.0% | Worker thread start-up/dispatch |
| 5.9% | Spinning on the keyspace lock inside `CommandTable.Execute` (`Lock.TryEnterSlow`, `Monitor.Enter_Slowpath`) |
| 3.1% | `Thread.PollGC` |
| 1.0% | Command code actually running (`CommandTable.Execute` and handlers, lock waits excluded) |

Two checks back this up:

1. **Command execution is cheap.** BenchmarkDotNet puts parse + execute + reply at about 0.1 µs per `GET`, roughly 3% of the 3.2 µs per request. Almost all of the CPU goes into moving bytes between the socket, three async loops and the thread pool.
2. **The spinning is real and removable.** Rerunning with `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0` ([cpu-experiment.csv](cpu-experiment.csv)) cut CPU from about 6.4 s to about 5.4 s per run (−15%, matching the profile) without lowering throughput. The server still uses about 3.4× Valkey's CPU.

The remaining gap is the architecture: each connection runs receive, process and send as separate async loops connected by two pipes, so every pipelined batch crosses threads at least twice, and all of them meet at one lock. Valkey reads, executes and writes on a single thread from one event loop. Closing that gap means changing the design (one thread owning the keyspace and the event loop, or sharded keyspaces), not tuning. The claims were measured with default runtime settings, and the spin-limit change was not kept.
