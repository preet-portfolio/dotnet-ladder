#!/usr/bin/env bash
# Downloads JSONTestSuite and the nativejson-benchmark corpus at pinned commits, then runs the judge.
set -euo pipefail

SUITE_SHA=1ef36fa01286573e846ac449e8683f8833c5b26a     # nst/JSONTestSuite, 2024-11-22
CORPUS_SHA=478d5727c2a4048e835a29c65adecc7d795360d5    # miloyip/nativejson-benchmark, 2022-10-28

here="$(cd "$(dirname "$0")" && pwd)"
vendor="$here/vendor"
mkdir -p "$vendor/corpus"

suite="$vendor/JSONTestSuite-$SUITE_SHA"
if [ ! -d "$suite" ]; then
  curl -fsSL "https://codeload.github.com/nst/JSONTestSuite/tar.gz/$SUITE_SHA" | tar -xz -C "$vendor"
fi

for f in twitter.json citm_catalog.json canada.json; do
  [ -f "$vendor/corpus/$f" ] || curl -fsSL "https://raw.githubusercontent.com/miloyip/nativejson-benchmark/$CORPUS_SHA/data/$f" -o "$vendor/corpus/$f"
done

dotnet run -c Release --project "$here/Ladder.Json.Judge" -- \
  "$suite/test_parsing" "$here/results" "$here/../evidence/divergences.md"
