#!/usr/bin/env bash
# Claim 8: replays Twitter production cache traces (twitter/cache-trace, Yang et al., OSDI 2020) against valkey-server
# and ours, each freshly started and empty, and compares every reply byte for byte (by 64-bit SHA-256 prefix; the
# replies that differ are then fetched in full and grouped by classify_replay.py).
# Needs: VALKEY_SERVER. Downloads the two pinned samples (~160 MB) into judge/vendor/ (gitignored).
set -euo pipefail
TRACE_SHA=1c503676d6972d4394a360e09162149a6c00f516   # twitter/cache-trace, samples/2020Mar
CLUSTERS=${CLUSTERS:-"cluster022 cluster014"}
here="$(cd "$(dirname "$0")" && pwd)"
root="$(cd "$here/../.." && pwd)"
vendor="$here/vendor/cache-trace-$TRACE_SHA"
out="${OUT:-$root/02-resp/evidence/trace-replay}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$vendor" "$out"
: "${VALKEY_SERVER:?set VALKEY_SERVER to the valkey-server binary}"

dotnet build -c Release "$root/02-resp/server/Ladder.Resp.Server" -v q -nologo >/dev/null
dotnet build -c Release "$root/02-resp/bench/Ladder.Resp.LoadGen" -v q -nologo >/dev/null
ours_dll="$root/02-resp/server/Ladder.Resp.Server/bin/Release/net10.0/Ladder.Resp.Server.dll"
gen() { dotnet "$root/02-resp/bench/Ladder.Resp.LoadGen/bin/Release/net10.0/Ladder.Resp.LoadGen.dll" "$@"; }

start() { # $1 = valkey|ladder, $2 = port; prints the pid
  case $1 in
    valkey) "$VALKEY_SERVER" --port "$2" --save '' --appendonly no >/dev/null 2>&1 & ;;
    ladder) dotnet "$ours_dll" "$2" >/dev/null 2>&1 & ;;
  esac
  local pid=$!
  for _ in $(seq 100); do (exec 3<>/dev/tcp/127.0.0.1/"$2") 2>/dev/null && break; sleep 0.1; done
  echo "$pid"
}
stop() { kill "$1" 2>/dev/null || true; for _ in $(seq 200); do kill -0 "$1" 2>/dev/null || return 0; sleep 0.1; done; kill -9 "$1" || true; }
replay() { # $1 = server, $2 = commands, $3 = hashes, rest = optional indices + dump
  local port=6396 pid; pid=$(start "$1" $port)
  gen replay $port "$2" "$3" "${@:4}"
  stop "$pid"
}

summary="$out/summary.md"
{
  echo "valkey: $("$VALKEY_SERVER" --version)"
  echo "trace: twitter/cache-trace @ $TRACE_SHA, samples/2020Mar"
  echo
} > "$summary"
for c in $CLUSTERS; do
  f="$vendor/$c"
  [ -f "$f" ] || curl -fsSL "https://raw.githubusercontent.com/twitter/cache-trace/$TRACE_SHA/samples/2020Mar/$c" -o "$f"
  echo "## $c (sha256 $(sha256sum "$f" | cut -c1-16)…)" >> "$summary"
  python3 -I "$here/trace_to_resp.py" "$f" "$work/$c.resp" 100000000 >> "$summary"
  replay valkey "$work/$c.resp" "$work/$c.valkey.bin" >> "$summary"
  replay ladder "$work/$c.resp" "$work/$c.ladder.bin" >> "$summary"
  if gen diff "$work/$c.valkey.bin" "$work/$c.ladder.bin" "$work/$c.mism" >> "$summary"; then
    echo "Byte-identical." >> "$summary"
  else
    # Replay again and keep the differing replies in full. A replay is deterministic, so the second run of each server
    # must produce the same hashes; that is checked too.
    replay valkey "$work/$c.resp" "$work/$c.valkey2.bin" "$work/$c.mism" "$work/$c.valkey.dump" > /dev/null
    replay ladder "$work/$c.resp" "$work/$c.ladder2.bin" "$work/$c.mism" "$work/$c.ladder.dump" > /dev/null
    cmp -s "$work/$c.valkey.bin" "$work/$c.valkey2.bin" && cmp -s "$work/$c.ladder.bin" "$work/$c.ladder2.bin" \
      && echo "(second replays reproduce the same hashes)" >> "$summary" || echo "WARNING: a second replay differed" >> "$summary"
    echo >> "$summary"
    python3 -I "$here/classify_replay.py" "$work/$c.resp" "$work/$c.mism" "$work/$c.valkey.dump" "$work/$c.ladder.dump" valkey ours >> "$summary"
  fi
  echo >> "$summary"
done
cat "$summary"
