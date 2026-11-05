# Mutation testing

**Result: Stryker reports 84.79%. Counting what the judge and fresh-process runs kill, the score is 89.4%. 47 mutants
survive. Each one is classified below: 36 are equivalent or only affect performance, 5 cannot be reached through the
public API, and 6 are real test gaps that are still open.**

**Re-run at `058a8e5`** (no source change since round 3; one more test since): Stryker with `coverage-analysis: off`
scores **85.01%** (380/447), and the same re-test kills 17 more in fresh processes and 1 with the spec suite; 2 don't
compile. That is 398/445 = **89.4%**, and the 47 survivors are exactly the ones classified below. Raw output:
[`stryker/mutation-report-058a8e5.md`](stryker/mutation-report-058a8e5.md), [`.json`](stryker/mutation-report-058a8e5.json)
and [`stryker/survivors-judged-058a8e5.csv`](stryker/survivors-judged-058a8e5.csv). The round-3 figures below are kept as they were.

This took three rounds. The first Stryker run scored **68.08%**. Re-testing its 143 survivors against the external judge
killed only 24. The rest were gaps in the tests, not equivalent mutants. Two rounds of new tests,
[`MutationGapTests.cs`](../tests/Ladder.DI.Tests/MutationGapTests.cs), raised the score to 84.79%. Most of the new tests
compare our behaviour with Microsoft's container: exception type and message, `IsService` answers, which instances are
shared, default parameter values.

## How it was run

```sh
cd 05-di
LADDER_DI_GRAPHS=1000 dotnet dotnet-stryker -f stryker-config.json --concurrency 2
```

- **Tools:** Stryker 5.0.0 with the `mtp` test runner, on the shared cloud VM (2 of its 4 vCPUs).
- **Graph count:** `LADDER_DI_GRAPHS=1000` cuts the differential property from 10,000 random graphs to 1,000 per mutant.
- **Judge re-test:** Stryker can only run test projects, so every survivor was then re-tested by
  [`tools/stryker/judge_survivors.py`](../../tools/stryker/judge_survivors.py). It applies one mutant at a time to the
  source and runs a command, then restores the file. The command,
  [`tools/stryker/check-05-mutant.sh`](../../tools/stryker/check-05-mutant.sh), does three things:
  1. builds;
  2. runs the unit tests in a **fresh process** with the full 10,000-graph property;
  3. runs the external judge (`judge/run-judge.sh`: `Specification.Tests` 10.0.13 against all three of our engines).
  A mutant counts as killed if either step 2 or step 3 fails.
- **Raw data:**
  - [`stryker/stryker-report-round3.md`](stryker/stryker-report-round3.md): Stryker's own table, round 3.
  - [`stryker/round1-survivors-vs-judge.csv`](stryker/round1-survivors-vs-judge.csv): round 1's 143 survivors against
    the judge.
  - [`stryker/survivors-final-pass.csv`](stryker/survivors-final-pass.csv): round 3's 68 survivors against fresh unit
    tests and the judge.

## Score

| | Mutants |
|---|---:|
| Killed by Stryker (round 3) | 379 |
| Survived or not covered (round 3) | 68 |
| … of which killed by the fresh-process unit tests | 17 |
| … of which killed by the spec suite | 1 |
| … of which do not compile when applied to the file by hand | 2 |
| … of which killed by one more test added after the final pass (`Keyed_resolution_after_the_container_is_disposed…`, checked by hand: passes on the real code, fails on the mutant) | 1 |
| **Survive everything** | **47** |
| Compile errors / ignored (Stryker) | 134 / 132 |

Adjusted score = 398 / (447 − 2) = **89.4%**. Stryker's own figure is 84.79%.

## What surprised us

- **The external judge kills few mutants.** Of round 1's 143 survivors, `Specification.Tests` killed 24. It never
  checked several things:
  - registration validation: removing the whole `Validate` call survived;
  - exception messages;
  - `IsService` with `AnyKey`;
  - disposal of factory-made transients;
  - keyed resolution after disposal.

  Passing 143/143 says the contract is met where it is tested, not that it is tested everywhere.
- **Stryker can report a mutant as surviving when it was never active.** `TypeNames.BuiltIn` is a static dictionary
  initializer. Stryker's `mtp` runner reuses one test process for many mutants, so the initializer runs once, and its
  mutants are never switched on. All 15 keyword-string mutants "survived" round 3 in Stryker. The same tests kill
  every one of them in a fresh process.
- **Kills by the random property are probabilistic.** Three mutants that round 2 killed survived round 3, with no
  change to the code under test: `Planner.cs` lines 85, 210 and 262. Whether the property kills them depends on which
  1,000 graphs it draws. The fresh-process pass with 10,000 graphs killed all three.

## Every surviving mutant

- **Equivalent:** behaviour cannot differ through any public API. [Likely] marks the ones where this is argued from the
  code but not proven by a test.
- **Performance only:** the mutant changes speed or memory use, never results.
- **Unreachable:** the code cannot run through the public API.
- **Test gap:** behaviour can differ and nothing tests it. These are the actionable ones.

| Id | Location | Mutation | Class | Why |
|---|---|---|---|---|
| 5 | Errors.cs:28 | `"Invalid service descriptor"` → `""` | Unreachable | Thrown only for a descriptor with no implementation type, factory or instance. `ServiceDescriptor`'s constructors make that impossible |
| 16 | Plan.cs:38 | `ScopeSlot` default `-1` → `+1` | Equivalent | Only scope-cached plans read `ScopeSlot`, and those always set it |
| 20 | Plan.cs:115 | `EnumerablePlan.MayNeedDisposal` → `true` | Equivalent | An array is never `IDisposable`, so `Capture` returns it unchanged |
| 113 | Planner.cs:203 | `anyKey ? Of(d) : itemId` → always `Of(d)` | Equivalent [Likely] | Changes the cache identity of an AnyKey-registered item in a specific-key enumeration. The new instance-sharing test matches Microsoft either way, so no difference is observable |
| 152 | Planner.cs:263 | do not reset enumerable location to `None` | Equivalent [Likely] | The location stays `Dispose`. Arrays don't need disposal, so `Dispose` without capture is `None` |
| 155 | Planner.cs:273 | drop `chain.Remove(id)` after planning an enumerable | Equivalent [Likely] | The enumerable plan is cached before any second visit, and the cache is checked before the cycle check |
| 234 | Planner.cs:416 | `GetCustomAttributes(true)` → `(false)` | Equivalent | `ParameterInfo` ignores `inherit` |
| 241 | Planner.cs:422 | under AnyKey, keep the `[ServiceKey]` parameter's declared type | **Test gap** | A `[ServiceKey] string` parameter resolved under AnyKey would throw instead of receiving the key. Untested: our new test's parameter is `object` |
| 248 | Planner.cs:431 | drop `break` after `[ServiceKey]` | **Test gap** (edge) | Differs only for a parameter that has both `[ServiceKey]` and `[FromKeyedServices]` |
| 254 | Planner.cs:448 | drop `break` after `[FromKeyedServices]` | **Test gap** (edge) | Same: differs only with two attributes on one parameter |
| 274 | Planner.cs:490 | `underlying is null` → `is not null` | Equivalent [Likely] | A `null` default for a struct parameter becomes `default(T)` in both engines anyway: reflection and expression invocation both turn `null` into `default` |
| 275 | Planner.cs:491 | drop `GetUninitializedObject` | Equivalent [Likely] | Same reason as 274 |
| 283 | Planner.cs:503 | every plan claims a scope slot | Performance only | Wasted slots; the numbering stays unique |
| 286 | Planner.cs:503 | slot `n − 1` → `n + 1` | Performance only | Shifts the numbering; slots stay unique |
| 287 | Planner.cs:503 | `-1` → `+1` for non-scoped plans | Equivalent | Non-scoped plans never read it |
| 369 | Interpreter.cs:17 | always `Capture` transients | Performance only | `Capture` ignores non-disposables |
| 384 | Interpreter.cs:58 | always allocate the argument array | Performance only | |
| 396 | Interpreter.cs:77 | "Unknown plan" message | Unreachable | Every plan type is handled |
| 398 | PlanCompiler.cs:28 | expression parameter name `"scope"` → `""` | Equivalent | Names in expression trees are cosmetic |
| 403 | PlanCompiler.cs:46 | never reuse a compiled body | Performance only | Recompiles each time |
| 404 | PlanCompiler.cs:49 | parameter name | Equivalent | As 398 |
| 409 | PlanCompiler.cs:86 | "Unknown plan" message | Unreachable | As 396 |
| 410 | PlanCompiler.cs:91 | constants typed `object` | Equivalent | `Cast` converts them to the needed type |
| 412–417 | PlanCompiler.cs:95–96 | when to insert `Expression.Convert` (5 mutants) | Equivalent | An extra or missing reference conversion is a no-op for reference types. Value types always take the `Convert` path in both versions |
| 435 | Scope.cs:76 | root scope caches scoped services in its own slot array instead of the singleton cache | Equivalent [Likely] | Both live exactly as long as the root |
| 441 | Scope.cs:84 | drop the lock-free read | Performance only | The locked path does the same thing |
| 462–464 | Scope.cs:103 | growth size of the slot array (3 mutants) | Performance only | `ScopeSlotCount ≥ slot + 1` always, so the array is still big enough |
| 472 | Scope.cs:120 | `service is null \|\| ReferenceEquals(service, this)` → `&&` | Equivalent [Likely] | A scope never reaches `Capture` with itself: `IServiceProvider` resolves through `ScopePlan`, which is never captured |
| 485 | Scope.cs:140 | don't dispose a service created during scope disposal | **Test gap** | Race: a scope is disposed while one of its services is being created. Not covered by any test |
| 487 | Scope.cs:144 | same, async-only disposable | **Test gap** | Same race |
| 488 | Scope.cs:147 | don't throw `ObjectDisposedException` in that race | **Test gap** | Same race |
| 517 | Scope.cs:187 | `ConfigureAwait(false)` → `true` | Equivalent | No synchronization context in this library |
| 556 | ServiceContainer.cs:122 | null key goes through the keyed path | Equivalent [Likely] | `ServiceId(null, T)` is the unkeyed id, so it gets the same plan and the same root cache. Only the accessor dictionary differs |
| 588 | ServiceContainer.cs:184 | no singleton shortcut accessor | Performance only | |
| 612 | ServiceContainer.cs:247 | compile on every call but the second | Performance only | Both engines give the same results (claim 2) |
| 624–626 | ServiceId.cs:27 | hash code variants (3 mutants) | Equivalent | Any hash is correct as long as equal ids hash equally. It may only add collisions |
| 631 | TypeNames.cs:15 | `"void"` → `""` | Unreachable | `void` cannot be a service or a type argument |
| 652 | TypeNames.cs:32 | print generic *parameter* names | Unreachable [Likely] | Only open generic types have them, and those never appear in a message: open generics can't be resolved or form cycles |
| 694 | TypeNames.cs:84 | `tick < 0` → `tick <= 0` | Equivalent | A type name never starts with a backtick |

Totals:
- Equivalent: 25, of which 8 are [Likely].
- Performance only: 11.
- Unreachable: 5.
- **Test gaps: 6.** The disposal race (3), `[ServiceKey]` with a non-object parameter under AnyKey (1), and two
  attributes on one parameter (2).

Open test gaps, in priority order:
1. **The disposal race** (485, 487, 488). It needs a deterministic test: a factory that blocks until the scope's
   `Dispose` has started.
2. **`[ServiceKey]` with a non-object parameter under AnyKey** (241). It should be compared with Microsoft's behaviour.
3. **Two attributes on one parameter** (248, 254). Compare with Microsoft.
