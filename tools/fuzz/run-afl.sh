#!/usr/bin/env bash
# Runs one SharpFuzz harness under AFL++ (claims 01/8 and 02/9).
# Usage: run-afl.sh <fuzz project dir> <library dll to instrument> <seed dir> <output dir> <seconds> [cpu]
# Needs: afl-fuzz (AFL++ 4.x), the sharpfuzz tool (dotnet tool install sharpfuzz.commandline --version 2.3.0).
set -euo pipefail
project=$1 lib=$2 seeds=$3 out=$4 seconds=$5 cpu=${6:-}
bin="$out/bin"
mkdir -p "$out"
dotnet publish -c Release "$project" -o "$bin" -v q -nologo >/dev/null
sharpfuzz "$bin/$lib" >/dev/null
harness="$bin/$(basename "$project").dll"
export AFL_SKIP_BIN_CHECK=1 AFL_SKIP_CPUFREQ=1 AFL_NO_UI=1 AFL_I_DONT_CARE_ABOUT_MISSING_CRASHES=1
bind=()
[ -n "$cpu" ] && bind=(-b "$cpu")
# -t: each input runs in a forked .NET process that has already JIT-compiled the harness; 10 s only guards hangs.
afl-fuzz "${bind[@]}" -i "$seeds" -o "$out/findings" -V "$seconds" -t 10000 -m none -- dotnet "$harness" > "$out/afl.log" 2>&1 || true
stats="$out/findings/default/fuzzer_stats"
grep -E '^(start_time|last_update|run_time|execs_done|execs_per_sec|corpus_count|saved_crashes|saved_hangs|bitmap_cvg|edges_found|total_edges|stability)' "$stats"
