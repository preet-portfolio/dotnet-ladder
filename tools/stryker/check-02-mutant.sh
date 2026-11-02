#!/usr/bin/env bash
# Kills a 02-resp mutant if the unit tests fail in a fresh process, or if the Valkey Tcl suite (the external judge,
# same files and tags as judge/run-judge.sh) fails a test that the unmutated server passes.
# BASELINE=1 records which suite tests the unmutated server passes, in $BASE. Exit: 0 survives, 1 killed, 3 build failure.
set -uo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
cd "$root/02-resp"
BASE=${BASE:-/tmp/ladder-resp-suite-baseline.txt}
FILES=(unit/type/incr unit/type/string unit/keyspace unit/expire)
TAGS=(--tags -needs:debug --tags -needs:repl --tags -needs:reset)
suite=$(ls -d judge/vendor/valkey-*/ | head -1)
port=6386
dotnet build -c Release tests/Ladder.Resp.Tests -v q -nologo > /tmp/b02.log 2>&1 || { cat /tmp/b02.log; exit 3; }
dotnet build -c Release server/Ladder.Resp.Server -v q -nologo > /tmp/b02s.log 2>&1 || { cat /tmp/b02s.log; exit 3; }
if [ -z "${BASELINE:-}" ] && ! dotnet tests/Ladder.Resp.Tests/bin/Release/net10.0/Ladder.Resp.Tests.dll > /tmp/u02.log 2>&1; then echo "KILLED-BY: unit"; exit 1; fi
dotnet server/Ladder.Resp.Server/bin/Release/net10.0/Ladder.Resp.Server.dll $port > /dev/null 2>&1 &
pid=$!
trap 'kill $pid 2>/dev/null; wait $pid 2>/dev/null' EXIT
for _ in $(seq 100); do (exec 3<>/dev/tcp/127.0.0.1/$port) 2>/dev/null && break; sleep 0.1; done
out=/tmp/suite02.txt
: > $out
for f in "${FILES[@]}"; do
  (cd "$suite" && VALKEY_BIN_DIR="$(dirname "$(command -v valkey-server)")" timeout 600 ./runtest --host 127.0.0.1 --port $port --single "$f" "${TAGS[@]}" --durable --timeout 120 2>&1) >> $out || true
done
passed=$(grep -E '^\[ok\]: ' $out | sed -E 's/ \([0-9]+ ms\)$//' | sort -u)
if [ -n "${BASELINE:-}" ]; then echo "$passed" > "$BASE"; echo "baseline: $(wc -l < "$BASE") passing"; exit 0; fi
lost=$(comm -23 "$BASE" <(echo "$passed"))
if [ -n "$lost" ]; then echo "KILLED-BY: judge ($(echo "$lost" | wc -l) lost: $(echo "$lost" | head -1 | cut -c1-40))"; exit 1; fi
