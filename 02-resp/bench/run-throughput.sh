#!/usr/bin/env bash
# Claim 3: valkey-benchmark against real valkey-server and against ours, same machine, same session.
# Rounds alternate between the servers so drift (thermal, background load) hits both equally; medians are reported.
set -euo pipefail
ROUNDS=${ROUNDS:-3}
REF_PORT=6391
OUR_PORT=6381
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
out="$root/02-resp/evidence"
mkdir -p "$out"

dotnet build -c Release "$root/02-resp/server/Ladder.Resp.Server" -v q -nologo >/dev/null
valkey-server --port $REF_PORT --save '' --appendonly no --daemonize yes >/dev/null
dotnet "$root/02-resp/server/Ladder.Resp.Server/bin/Release/net10.0/Ladder.Resp.Server.dll" $OUR_PORT >/dev/null 2>&1 &
OUR_PID=$!
trap 'valkey-cli -p $REF_PORT shutdown nosave >/dev/null 2>&1 || true; kill $OUR_PID 2>/dev/null || true' EXIT
for p in $REF_PORT $OUR_PORT; do for _ in $(seq 50); do valkey-cli -p $p ping >/dev/null 2>&1 && break; sleep 0.1; done; done

raw="$out/throughput-raw.csv"
echo "round,server,test,rps,avg_latency_ms,p50_latency_ms,p99_latency_ms" > "$raw"
for round in $(seq "$ROUNDS"); do
  for server in valkey ladder; do
    port=$([ $server = valkey ] && echo $REF_PORT || echo $OUR_PORT)
    valkey-cli -p $port flushall >/dev/null
    # Warm-up pass so the JIT has compiled the hot paths before the measured pass.
    valkey-benchmark -p $port -t set,get -n 200000 -c 50 -P 16 -q >/dev/null
    valkey-benchmark -p $port -t set,get -n 1000000 -c 50 -P 16 --csv \
      | tail -n +2 | tr -d '"' | awk -F, -v r=$round -v s=$server '{print r","s","$1","$2","$3","$5","$7}' >> "$raw"
  done
done
echo "machine: $(sysctl -n machdep.cpu.brand_string), $(sysctl -n hw.ncpu) cores; valkey-server $(valkey-server --version | awk '{print $3}'); $(dotnet --version)" > "$out/throughput-env.txt"
cat "$raw"
