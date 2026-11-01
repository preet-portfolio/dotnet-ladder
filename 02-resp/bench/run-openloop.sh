#!/usr/bin/env bash
# Claims 5-7: fixed-rate (open-loop) GET load against valkey-server, Garnet and ours, same machine, same session.
# The servers share CPUs 0-1 and the load generator gets CPUs 2-3 (taskset), so the generator's spinning never
# competes with the server it measures. Rounds alternate between servers; medians are reported by openloop_report.py.
# Needs: VALKEY_SERVER (path to valkey-server), GARNET (path to the GarnetServer executable or "dotnet <dll>"), taskset.
set -euo pipefail
ROUNDS=${ROUNDS:-3}
SECONDS_MEASURED=${SECONDS_MEASURED:-20}
WARMUP=${WARMUP:-5}
CONNS=${CONNS:-50}
KEYS=${KEYS:-100000}
VALUE_BYTES=${VALUE_BYTES:-64}
FRACTIONS=${FRACTIONS:-"0.25 0.5 0.75"}
SERVER_CPUS=${SERVER_CPUS:-0,1}
CLIENT_CPUS=${CLIENT_CPUS:-2,3}
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="${OUT:-$root/02-resp/evidence}"
mkdir -p "$out"
: "${VALKEY_SERVER:?set VALKEY_SERVER to the valkey-server binary}"
: "${GARNET:?set GARNET to the Garnet server command}"

dotnet build -c Release "$root/02-resp/server/Ladder.Resp.Server" -v q -nologo >/dev/null
dotnet build -c Release "$here/Ladder.Resp.LoadGen" -v q -nologo >/dev/null
ours_dll="$root/02-resp/server/Ladder.Resp.Server/bin/Release/net10.0/Ladder.Resp.Server.dll"
gen() { taskset -c "$CLIENT_CPUS" dotnet "$here/Ladder.Resp.LoadGen/bin/Release/net10.0/Ladder.Resp.LoadGen.dll" "$@"; }

port_of() { case $1 in valkey) echo 6393 ;; garnet) echo 6394 ;; ladder) echo 6395 ;; esac; }
start_server() { # $1 = name; prints the pid
  local port; port=$(port_of "$1")
  case $1 in
    valkey) taskset -c "$SERVER_CPUS" "$VALKEY_SERVER" --port "$port" --save '' --appendonly no >/dev/null 2>&1 & ;;
    garnet) taskset -c "$SERVER_CPUS" $GARNET --port "$port" --bind 127.0.0.1 >/dev/null 2>&1 & ;;
    ladder) taskset -c "$SERVER_CPUS" dotnet "$ours_dll" "$port" >/dev/null 2>&1 & ;;
  esac
  local pid=$!
  for _ in $(seq 100); do (exec 3<>/dev/tcp/127.0.0.1/"$port") 2>/dev/null && break; sleep 0.1; done
  echo "$pid"
}
stop_server() { # the server runs inside $(...), so it is not this shell's child: wait for it to exit by polling
  kill "$1" 2>/dev/null || true
  for _ in $(seq 200); do kill -0 "$1" 2>/dev/null || return 0; sleep 0.1; done
  kill -9 "$1" 2>/dev/null || true
}
cpu_ticks() { awk '{print $14 + $15}' "/proc/$1/stat"; }   # utime + stime, in clock ticks
clk_tck=$(getconf CLK_TCK)

raw="$out/openloop-raw.csv"
cpu="$out/openloop-cpu.csv"
rm -f "$raw"
echo "round,server,rate,requests,cpu_seconds,cpu_us_per_request" > "$cpu"
maxes="$out/openloop-closed-max.csv"
echo "round,server,closed_loop_rps" > "$maxes"

for round in $(seq "$ROUNDS"); do
  # The rates are fractions of Valkey's closed-loop maximum measured in the same round.
  pid=$(start_server valkey); gen load "$(port_of valkey)" "$KEYS" "$VALUE_BYTES" >/dev/null
  gen closed "$(port_of valkey)" "$CONNS" 3 "$KEYS" >/dev/null   # warm-up
  vmax=$(gen closed "$(port_of valkey)" "$CONNS" 10 "$KEYS" | awk '{print $(NF-1)}')
  stop_server "$pid"
  echo "$round,valkey,$vmax" >> "$maxes"
  for server in valkey garnet ladder; do
    pid=$(start_server $server); port=$(port_of $server)
    gen load "$port" "$KEYS" "$VALUE_BYTES" >/dev/null
    gen closed "$port" "$CONNS" 5 "$KEYS" >/dev/null   # JIT warm-up for the .NET servers; same for all
    [ $server = valkey ] || echo "$round,$server,$(gen closed "$port" "$CONNS" 10 "$KEYS" | awk '{print $(NF-1)}')" >> "$maxes"
    for f in $FRACTIONS; do
      rate=$(awk -v m="$vmax" -v f="$f" 'BEGIN{printf "%d", m*f}')
      before=$(cpu_ticks "$pid")
      gen open "$port" "$rate" "$SECONDS_MEASURED" "$WARMUP" "$CONNS" "$KEYS" "$raw" "$round:$server:$f" >/dev/null
      after=$(cpu_ticks "$pid")
      requests=$(awk -v r="$rate" -v s="$SECONDS_MEASURED" -v w="$WARMUP" 'BEGIN{printf "%d", r*(s+w)}')
      awk -v r="$round" -v s="$server" -v rate="$rate" -v n="$requests" -v b="$before" -v a="$after" -v t="$clk_tck" \
        'BEGIN{c=(a-b)/t; printf "%s,%s,%s,%d,%.2f,%.2f\n", r, s, rate, n, c, c*1e6/n}' >> "$cpu"
    done
    stop_server "$pid"
  done
done

{
  echo "cpu: $(grep -m1 'model name' /proc/cpuinfo | cut -d: -f2 | xargs), $(nproc) vCPUs ($(systemd-detect-virt 2>/dev/null || echo unknown) guest)"
  echo "kernel: $(uname -r)"
  echo "servers on CPUs $SERVER_CPUS, load generator on CPUs $CLIENT_CPUS"
  echo "valkey: $("$VALKEY_SERVER" --version)"
  echo "garnet: $GARNET ($($GARNET --version 2>/dev/null | head -1 || true))"
  echo "dotnet: $(dotnet --version)"
  echo "load: $CONNS connections, $KEYS keys of $VALUE_BYTES bytes, uniform random GET, ${WARMUP}s warm-up + ${SECONDS_MEASURED}s measured per rate, $ROUNDS rounds"
  echo "loadavg at end: $(cat /proc/loadavg)"
} > "$out/openloop-env.txt"
cat "$cpu"
