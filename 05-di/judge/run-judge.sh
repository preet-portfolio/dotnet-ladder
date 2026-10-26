#!/usr/bin/env bash
# External judge for 05-di. Runs Microsoft.Extensions.DependencyInjection.Specification.Tests, the conformance suite
# third-party containers run, against Microsoft's own ServiceProvider (the reference) and against ours with each of
# its three resolution engines, then compares them test by test.
# The suite is a NuGet package pinned in 05-di/Directory.Packages.props (10.0.13, MIT), restored like any package;
# nothing is downloaded by hand. Writes judge/results/spec-<subject>.txt (one verdict per test) and summary.txt.
# Exits non-zero if any engine of ours fails a test the reference passes.
set -euo pipefail

here="$(cd "$(dirname "$0")" && pwd)"
dotnet run -c Release --project "$here/Ladder.DI.Judge" -- "$here/results"
