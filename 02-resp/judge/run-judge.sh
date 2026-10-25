#!/usr/bin/env bash
# External judge for 02-resp. Runs Valkey's own Tcl test suite (pinned) in external-server mode against
# real valkey-server and against ours, with the same tag filter, then compares them test by test.
# Then loads the real GeoNames cities15000 dataset into both through valkey-cli --pipe and diffs full dumps.
# Needs: valkey-server, valkey-cli (Homebrew "valkey" 9.1.2), tclsh, python3, curl, unzip.
set -euo pipefail

VALKEY_SHA=7f1dffedff6de73058b2c2a389422b6ecd56c8fb   # valkey-io/valkey tag 9.1.2
FILES=(unit/type/incr unit/type/string unit/keyspace unit/expire)
TAGS=(--tags -needs:debug --tags -needs:repl --tags -needs:reset)
# --durable: an exception (e.g. an unsupported command) fails that one test instead of aborting the rest of the file.
REF_PORT=6390
OUR_PORT=6380

here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
vendor="$here/vendor"
results="$here/results"
mkdir -p "$vendor" "$results"

suite="$vendor/valkey-$VALKEY_SHA"
if [ ! -d "$suite" ]; then
  curl -fsSL "https://codeload.github.com/valkey-io/valkey/tar.gz/$VALKEY_SHA" | tar -xz -C "$vendor"
fi
bindir="$(dirname "$(command -v valkey-server)")"

dotnet build -c Release "$root/02-resp/server/Ladder.Resp.Server" -v q -nologo >/dev/null
ours_dll="$root/02-resp/server/Ladder.Resp.Server/bin/Release/net10.0/Ladder.Resp.Server.dll"

stop_servers() {
  valkey-cli -p $REF_PORT shutdown nosave >/dev/null 2>&1 || true
  [ -n "${OUR_PID:-}" ] && kill "$OUR_PID" 2>/dev/null || true
}
trap stop_servers EXIT

wait_port() { for _ in $(seq 50); do valkey-cli -p "$1" ping >/dev/null 2>&1 && return 0; sleep 0.1; done; echo "server on $1 did not start" >&2; exit 2; }

valkey-server --port $REF_PORT --save '' --appendonly no --enable-debug-command yes --daemonize yes >/dev/null
dotnet "$ours_dll" $OUR_PORT >/dev/null 2>&1 &
OUR_PID=$!
wait_port $REF_PORT
wait_port $OUR_PORT

run_suite() { # $1 = label, $2 = port
  local out="$results/suite-$1.txt"
  : > "$out"
  for f in "${FILES[@]}"; do
    echo "=== $f" >> "$out"
    (cd "$suite" && VALKEY_BIN_DIR="$bindir" ./runtest --host 127.0.0.1 --port "$2" --single "$f" "${TAGS[@]}" --durable --timeout 120 2>&1) >> "$out" || true
  done
}

echo "running Valkey test suite against valkey-server $(valkey-server --version | awk '{print $3}')..."
run_suite reference $REF_PORT
echo "running Valkey test suite against ladder-resp..."
run_suite ours $OUR_PORT

# --- GeoNames differential -------------------------------------------------------------------------
geo="$vendor/geonames"
mkdir -p "$geo"
if [ ! -f "$geo/cities15000.txt" ]; then
  curl -fsSL https://download.geonames.org/export/dump/cities15000.zip -o "$geo/cities15000.zip"
  unzip -o -q "$geo/cities15000.zip" -d "$geo"
fi
shasum -a 256 "$geo/cities15000.zip" | awk '{print $1}' > "$results/geonames-sha256.txt"
python3 -I "$here/geonames_to_resp.py" "$geo/cities15000.txt" "$geo/load.resp"

for port in $REF_PORT $OUR_PORT; do
  valkey-cli -p $port flushall >/dev/null
  valkey-cli -p $port --pipe < "$geo/load.resp" > "$results/geonames-load-$port.txt"
  # Dump every key with its value, sorted, so the two servers can be compared byte for byte.
  valkey-cli -p $port --scan --count 1000 | LC_ALL=C sort > "$geo/keys-$port.txt"
  python3 -I "$here/dump_values.py" $port "$geo/keys-$port.txt" "$geo/dump-$port.txt"
done

python3 -I "$here/compare.py" "$results" "$geo/dump-$REF_PORT.txt" "$geo/dump-$OUR_PORT.txt" "${FILES[@]}"
