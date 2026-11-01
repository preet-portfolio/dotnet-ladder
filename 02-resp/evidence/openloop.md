# Open-loop latency and CPU per request (claims 5–7)

**Read this first:**
- **This compares against Valkey 7.2.13, not 9.1.2.** 7.2.13 is the Ubuntu 24.04 package, used as a stand-in.
  The session was not allowed to build Valkey 9.1.2 from source, so claims 5–7 *as written* (against Valkey 9.1.2)
  stay pending. Rerun `bench/run-openloop.sh` with `VALKEY_SERVER` pointing at a 9.1.2 build to settle them.
- **The p99 and p99.9 tails here are mostly the VM, not the servers.**
  - In every 25-second run the load generator itself stalled for 20–30 ms at least once (`max_send_lag_us`). That
    happened even at 25% load, against every server.
  - 0.3–6% of sends left more than 1 ms late.
  - Latency is measured from each request's *scheduled* time, so these stalls are charged to whichever server was
    running. That is correct for avoiding coordinated omission, but it means tail differences of less than about 2×
    between servers are not meaningful on this machine.
  - p50 and CPU per request are much less affected.

## Setup

- Machine: shared cloud VM, Intel Xeon Processor @ 2.10GHz, 4 vCPUs, kernel 6.18.44. Each server was pinned to CPUs 0–1
  and the load generator to CPUs 2–3 (`taskset`). Nothing else heavy was running.
- Load: `GET` of uniformly random keys over 50 connections, one request outstanding per connection, no pipelining.
  100,000 keys of 64 bytes. Each rate had 5 s of warm-up and then 20 s measured. There were 3 rounds, and each server
  was restarted for each round.
- Rates: 25%, 50% and 75% of Valkey's closed-loop maximum, measured at the start of each round.
- Latency: from the scheduled send time to the reply, recorded with HdrHistogram.
- CPU: the server process's utime+stime from `/proc/<pid>/stat`, over the warm-up plus the measured run, divided by
  the requests sent.
- Raw data: [`openloop/`](openloop/). It holds `openloop-raw.csv` (one row per run), `openloop-cpu.csv`,
  `openloop-closed-max.csv` and `openloop-env.txt`.

Closed-loop maximum (req/s; rounds 1, 2, 3):

| Server | Rounds 1, 2, 3 (req/s) |
|---|---|
| Valkey 7.2.13 | 74,460, 69,195, 75,169 |
| Garnet 2.2.5 | 94,571, 111,815, 106,004 |
| ours | 97,988, 94,335, 100,051 |

## Results

Each cell is the median over 3 rounds, with the min–max in brackets (`bench/openloop_report.py`):

| Load | Server | Rate (req/s) | p50 µs | p99 µs | p99.9 µs | max µs | CPU µs/req | Late sends | Rounds |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 25% | valkey | 18,615 | 45 (43–45) | 1,042 (724–1,419) | 5,370 (4,374–6,492) | 11,756 (9,560–17,039) | 24.70 (23.22–25.42) | 4227 | 3 |
| 25% | garnet | 18,615 | 52 (48–52) | 939 (798–1,309) | 3,699 (2,908–4,334) | 8,065 (7,270–9,216) | 78.31 (77.58–81.65) | 5645 | 3 |
| 25% | ladder | 18,615 | 52 (50–59) | 1,124 (1,070–1,492) | 3,908 (3,818–6,169) | 10,068 (9,191–17,809) | 82.53 (77.25–83.40) | 5852 | 3 |
| 50% | valkey | 37,230 | 40 (39–41) | 2,124 (1,416–3,058) | 16,318 (3,539–22,954) | 39,584 (5,484–44,401) | 20.35 (20.07–20.60) | 15651 | 3 |
| 50% | garnet | 37,230 | 55 (54–55) | 1,720 (1,648–86,114) | 6,545 (4,915–184,025) | 12,739 (7,991–234,619) | 49.93 (49.79–53.80) | 30104 | 3 |
| 50% | ladder | 37,230 | 64 (61–65) | 2,118 (2,023–3,039) | 7,234 (5,947–56,066) | 28,049 (18,465–74,973) | 48.54 (48.51–52.55) | 23745 | 3 |
| 75% | valkey | 55,845 | 44 (41–56) | 2,607 (2,290–87,360) | 9,945 (5,886–253,755) | 27,574 (14,541–272,368) | 16.64 (16.49–17.40) | 40559 | 3 |
| 75% | garnet | 55,845 | 58 (55–58) | 3,092 (2,378–6,275) | 9,634 (6,783–41,189) | 25,788 (13,427–45,744) | 32.69 (32.18–36.10) | 81931 | 3 |
| 75% | ladder | 55,845 | 80 (63–89) | 4,366 (2,836–37,290) | 10,388 (7,446–61,407) | 22,708 (17,056–70,189) | 31.78 (31.38–35.15) | 121938 | 3 |

## Claims (against the Valkey 7.2.13 stand-in)

| # | Check | 25% | 50% | 75% | Verdict vs 7.2.13 |
|---|---|---:|---:|---:|---|
| 5 | ours p99 / Valkey p99 (≤ 2) | 1.08 | 1.00 | 1.67 | Holds |
| 5 | ours p99.9 / Valkey p99.9 (≤ 3) | 0.73 | 0.44 | 1.04 | Holds |
| 6 | ours CPU per request / Valkey's (≤ 4) | 3.34 | 2.39 | 1.91 | Holds |
| 6 | prediction: the ratio is worst at the lowest rate | 3.34 | > 2.39 | > 1.91 | Holds |
| 7 | prediction: Garnet CPU per request < ours | 78.3 < 82.5 | 49.9 > 48.5 | 32.7 > 31.8 | **Fails** at 50% and 75% |

On the claims:

- **Claim 7.** Garnet and ours use about the same CPU per request at every rate: within 5%, and inside the
  round-to-round spread. The prediction was that Garnet would be clearly cheaper, and it was not.
  - What this shows: CPU per request on this machine is dominated by a cost that both C# servers share and Valkey does
    not.
  - What that cost is, is a guess [Guessing]: spinning thread-pool threads, and socket readiness handled by the .NET
    runtime.
- **Claim 6.** At 25% load each .NET server spends about 80 µs of CPU per request, against 25 µs for Valkey. At 75% load
  the figures fall to about 32 µs and 17 µs. This fits a fixed cost that is spent whether or not a request arrives
  (spinning), spread over more requests as the rate rises.
- **p50.** Ours is 52/64/80 µs at 25/50/75% load. Valkey is 40–45 µs. Garnet is 52–58 µs.
  - Our median rises with load more than the other two do: at 75%, ours is 80 µs and Garnet's is 58 µs.
  - This is the one latency difference that is clearly larger than the VM noise.
