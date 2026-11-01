#!/usr/bin/env bash
# Exit 1 if the unit tests (fresh process, full 10,000-graph property) or the spec-suite judge fail; 3 on build failure.
cd "$(cd "$(dirname "$0")/../../05-di" && pwd)"
dotnet build -c Release tests/Ladder.DI.Tests -v q -nologo > /tmp/b05.log 2>&1 || { cat /tmp/b05.log; exit 3; }
if ! dotnet tests/Ladder.DI.Tests/bin/Release/net10.0/Ladder.DI.Tests.dll > /tmp/u05.log 2>&1; then echo "KILLED-BY: unit"; exit 1; fi
if ! dotnet run -c Release --project judge/Ladder.DI.Judge -- /tmp/judge05 > /tmp/j05.log 2>&1; then grep -q "error CS" /tmp/j05.log && { cat /tmp/j05.log; exit 3; }; echo "KILLED-BY: judge"; exit 1; fi
