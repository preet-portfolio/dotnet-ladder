#!/usr/bin/env bash
# Rebuilds site/ from the repo's evidence and deploys it to Vercel production (https://dotnet-ladder.vercel.app).
# The private claude.ai copy is site/artifact.html; Claude republishes it to the same URL.
set -euo pipefail
root="$(cd "$(dirname "$0")/../.." && pwd)"
python3 "$root/tools/site/build.py"
(cd "$root/site" && vercel deploy --prod --yes)
