#!/usr/bin/env bash
# Runs the same steps as .github/workflows/ci.yml on this machine. Pass --deep to add benchmarks and Stryker.
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
cd "$root"
projects=(01-json 02-resp)

command -v actionlint >/dev/null && actionlint .github/workflows/ci.yml
dotnet tool restore
dotnet build Ladder.slnx -c Release
dotnet test --solution Ladder.slnx -c Release --no-build
for p in "${projects[@]}"; do
  "$p/judge/run-judge.sh"
  if [ "${1:-}" = "--deep" ]; then
    dotnet run -c Release --project "$p"/bench/*.Bench -- --filter '*'
    (cd "$p" && dotnet dotnet-stryker -f stryker-config.json)
  fi
done
