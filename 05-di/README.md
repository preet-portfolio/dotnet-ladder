# 05 · Dependency injection container

**Concept:** lifetimes, scopes and disposal order. That means building an `IServiceProvider` from an `IServiceCollection` that hands out singleton, scoped and transient services with the right identity, disposes what it created in reverse order of creation (including `IAsyncDisposable`), refuses cycles and captive dependencies, and resolves open generics, `IEnumerable<T>` and keyed services. Then compiling each resolution plan to a delegate so the hot path does no lookups or allocations beyond the service itself.
**Spec:** the `Microsoft.Extensions.DependencyInjection` contract: the `Microsoft.Extensions.DependencyInjection.Abstractions` 10.0.13 interfaces (`IServiceProvider`, `IKeyedServiceProvider`, `IServiceScopeFactory`, `IServiceProviderIsService`, `IServiceProviderIsKeyedService`, `[ServiceKey]`, `[FromKeyedServices]`), the docs at https://learn.microsoft.com/dotnet/core/extensions/dependency-injection/ (service lifetimes, constructor selection rules, disposal guidelines, scope validation), and above all the conformance suite third-party containers (Autofac, DryIoc, Lamar, Grace) run: `Microsoft.Extensions.DependencyInjection.Specification.Tests` 10.0.13.
**Source read first:** `dotnet/runtime` `release/10.0` (commit `00a171f`), `src/libraries/Microsoft.Extensions.DependencyInjection/src/`:
- `ServiceLookup/CallSiteFactory.cs`: constructor selection, open generics, the "slot" scheme that gives the last registration and the last `IEnumerable<T>` element the same instance, `AnyKey`;
- `CallSiteRuntimeResolver.cs`: which scope a singleton's dependencies are captured in;
- `CallSiteValidator.cs`: captive-dependency checks;
- `CallSiteChain.cs`: cycle detection;
- `ServiceProviderEngineScope.cs`: disposal order, `IAsyncDisposable`-only services;
- `DynamicServiceProviderEngine.cs`: interpret first, compile on the second call.

Every test in the specification suite was read too, with its `Fakes/`.

## Claims (committed in `11f8553`, before any container code existed)

| # | Claim | Judge | Result |
|---|---|---|---|
| 1 | Of the tests in `Microsoft.Extensions.DependencyInjection.Specification.Tests` 10.0.13 (`DependencyInjectionSpecificationTests` and `KeyedDependencyInjectionSpecificationTests`, all partials) that Microsoft's own container passes, ours passes **100%** | The suite, run by its own xUnit v2 runner against both containers, compared test by test | **Held.** 143/143, the same as Microsoft's 143/143, with each of our three resolution engines; 0 differences test by test |
| 2 | On **10,000** random service graphs (lifetimes, factories, instances, open generics, `IEnumerable<T>`, keys, nested scopes, cycles, missing dependencies), ours and Microsoft's container agree on **every** observation: which resolutions return the same instance, the order in which disposables are disposed, and the exception type thrown | Microsoft's `ServiceProvider` 10.0.13 as reference implementation, driven by CsCheck | **Held, after two fixes the test forced.** 10,000/10,000 per run; eight runs on fresh seeds, 80,000 graphs, 0 disagreements. The first runs found two places where we differed from Microsoft; we now match it, quirks included. See below. |
| 3 | Once warm, resolving a singleton or scoped service allocates **0 B**, and resolving a transient allocates only the object graph it returns | BenchmarkDotNet `Allocated` column and a `GC.GetAllocatedBytesForCurrentThread` unit test | **Held** (unit test): 0 B for singleton and scoped; a three-level transient graph allocates exactly what `new` does. BenchmarkDotNet's dry run agrees (0 / 0 / 24 / 112 B, same as Microsoft). The full BenchmarkDotNet run is pending the lead's quiet-machine run. |
| 4 | Warm resolution is no slower than **1.25×** Microsoft's container (which compiles to IL) for a singleton, a scoped service, a transient, and a transient with a 3-level dependency graph | BenchmarkDotNet, ours vs `ServiceProvider` | **Pending the lead's quiet-machine run.** |

## Decision

- **Plan once, then serve from an accessor.** `Planner` turns a service request into a plan tree (constructor, factory, constant, enumerable, the scope itself). It follows `CallSiteFactory`'s rules exactly:
  - the last registration wins;
  - the longest constructor whose parameters can all be satisfied is chosen, and any other satisfiable constructor must take a subset of its parameter types, otherwise it's ambiguous;
  - an exact registration beats an open generic, and only the last open generic is tried;
  - `AnyKey` registrations answer any key but are never enumerated;
  - plans are cached by (service, slot).

  Cycles and unresolvable constructors fail while planning, before anything is constructed. The container caches one accessor per service, with unkeyed lookups in a dictionary keyed by `Type` alone, so a warm resolution is one dictionary probe and one delegate call.
- **Interpret the first call, compile the second.** The interpreter walks the plan with reflection. On the second resolution the plan is compiled to a `Func<Scope, object?>` with expression trees: `new` calls nested in place, singletons embedded as constants, scoped services as a call to the scope's slot lookup. A transient is wrapped in the disposal-capture call only if its type can be disposable, which a constructor plan knows statically. Microsoft compiles on a thread-pool thread and swaps the result in later; ours compiles inline on the second call. That costs that caller once, but there is never a window where two engines serve the same service. A singleton's accessor is just its value.
- **Where instances live.** A singleton's value sits on its plan, read without a lock and created under a lock on the plan. A scoped instance sits in the scope's array, at a slot the plan claimed when it was built. Microsoft uses a dictionary per scope. Reads are lock-free, and writes happen once, under the scope's lock. A scoped service resolved from the root lives in the root like a singleton. A singleton is always created in the root scope: its transient dependencies are captured by the root, and an `IServiceProvider` it receives is the root, whichever scope asked first.
- **Disposal is the reverse of capture.** A service is captured after its constructor returns, so its dependencies are captured before it and disposed after it. Instances the caller registered are never disposed. Sync `Dispose` throws on an `IAsyncDisposable`-only service, as Microsoft's does. Disposing the root disposes the container. Scopes are flat: disposing one never touches another.
- **Matching Microsoft includes its quirks.** Claim 2 says "every observation", and the differential test found two places where Microsoft's behaviour isn't what a clean design would do. We copy both on purpose, each pinned by a test that runs against both containers: instance identity keyed by (service, slot), and the scope validator's memo keyed by `ResultCache.Key`.
- **The judge is a console app running xUnit v2.** The specification package is built on xUnit v2 (`xunit.extensibility.core` 2.9.3), and a v3 project can't discover its tests. `global.json` makes `dotnet test` Microsoft.Testing.Platform-only, and xUnit v2 has no first-party MTP runner. So `judge/Ladder.DI.Judge` subclasses every spec class four times (Microsoft's container, and ours with each engine) and runs itself with xUnit v2's own `AssemblyRunner`, writing one verdict per test.
- **Not built:** Microsoft's `StackGuard`, which moves call-site construction onto a fresh thread when the stack runs low, so a dependency chain thousands deep overflows here and doesn't there. Also missing: Native AOT and trimming support (we compile with `Expression.Compile`), `DependencyInjectionEventSource`, and the debugger views.

## Evidence

- **Judge:** [`judge/results/summary.txt`](judge/results/summary.txt), with one verdict per test per subject in `judge/results/spec-*.txt`. `judge/run-judge.sh` runs it; the suite is the pinned NuGet package. To check the harness can fail, disposal order was temporarily reversed: `DisposesInReverseOrderOfCreation` failed for all three engines and nothing else did.

  | Subject | Passed | Failed |
  |---|---:|---:|
  | Microsoft `ServiceProvider` 10.0.13 | 143 | 0 |
  | Ours, compiled on second call (default) | 143 | 0 |
  | Ours, compiled on first call | 143 | 0 |
  | Ours, interpreted | 143 | 0 |

  That is 89 tests in `DependencyInjectionSpecificationTests` (with its `ActivatorUtilities` and `IServiceProviderIsService` partials) and 54 in `KeyedDependencyInjectionSpecificationTests` (with `IServiceProviderIsKeyedService`), theory rows counted separately.
- **Differential testing:** [`evidence/differential.md`](evidence/differential.md) covers the generator, what is compared, the per-event coverage of the final run, and what the test found.
- **Allocation and speed:** [`evidence/benchmarks.md`](evidence/benchmarks.md). Allocation is verified; timing is pending.
- **Tests:** 26 test cases from 18 methods: the differential property, allocation tests, and lifecycle tests (disposal order, `IAsyncDisposable`, captive dependencies, `ValidateOnBuild`, cycle paths, use after dispose, concurrent first resolution in every engine, scope growth). Four methods run against both containers; three of those pin a Microsoft quirk.
- **Mutation testing:** [`evidence/stryker.md`](evidence/stryker.md), pending.

## What surprised us

- **The conformance suite passed on the first run, and the differential test failed on its first run.** 143/143 said the container was done. The first 10,000 random graphs disagreed with Microsoft's container within seconds. The suite tests each behaviour on its own; it never resolves the same service two different ways in one provider, and never combines keyed instances with scope validation.
- **Microsoft's resolution depends on order.** Instances are keyed by (service, slot), and a single resolution reads slot 0. Register two open generics for `IS<>`, the last one constrained so it rejects `K1`. Then `GetService<IS<K1>>()` throws `ArgumentException`. But if `GetServices<IS<K1>>()` runs first, it puts the open generic that fits into slot 0, and the same `GetService` call returns it. We had keyed instances by registration, which passes all 143 spec tests. Now we key them by slot, like Microsoft.
- **Microsoft's scope validation has a false positive.** The validator remembers each call site's result by `ResultCache.Key`, and every instance registration, keyed or not, gets the key (unkeyed service type, 0). So once a scoped `IS<K1>` has been validated, a singleton that depends on a *keyed instance* `IS<K1>` is reported as a captive dependency. We reproduce it, so the two containers refuse the same builds.
- **The constraint violation in the first divergence was itself a Microsoft decision.** For a single service it rethrows `MakeGenericType`'s `ArgumentException`, while `IEnumerable<T>` silently skips the registration. An `AnyKey` enumeration also skips keyed open generics entirely, because it asks to close the open definition with no type arguments. Neither is documented; both follow from `CallSiteFactory`, and both are tested against Microsoft's container.
- **A spec package can force its test framework on you.** The specification tests are compiled against xUnit v2. That is why the judge is a console app with xUnit v2's in-process runner, not a test project.
