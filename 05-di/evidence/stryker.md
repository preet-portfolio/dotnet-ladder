# Mutation testing

**Status: pending the lead's run.** It wasn't run here because the machine was shared.

```sh
cd 05-di
LADDER_DI_GRAPHS=1000 dotnet dotnet-stryker -f stryker-config.json
```

`stryker-config.json` mutates `src/Ladder.DI` and runs `tests/Ladder.DI.Tests` through Microsoft.Testing.Platform (`test-runner: mtp`; with the old VSTest runner, 01-json's first Stryker run scored 0%). The differential property checks 10,000 random graphs per run, about 5 s. `LADDER_DI_GRAPHS=1000` cuts that tenfold for each mutant. The property's coverage floors scale with the graph count, so it still fails if the generator stops reaching a region.

The specification suite is not part of the Stryker run, because it is a console judge, not a test project. A mutant that survives the unit and differential tests but would fail the conformance suite therefore counts as a survivor here. Check survivors against `judge/run-judge.sh` before calling them equivalent.
