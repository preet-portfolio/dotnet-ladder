# Differential testing against Microsoft's container (claim 2)

`tests/Ladder.DI.Tests/DifferentialTests.cs`. Reference implementation: `Microsoft.Extensions.DependencyInjection` 10.0.13 (`ServiceProvider`), the same version as the specification suite.

## What is generated

CsCheck draws a *scenario*: up to 14 registrations over six services `IS<K0>`..`IS<K5>`, a script of up to 30 operations, the two validation options, our resolution engine (compiled on second call, compiled on first call, interpreted) and whether the container is disposed synchronously or asynchronously at the end.

- **Registrations** pick a lifetime, a key (none, `"k1"`, `"k2"`, `KeyedService.AnyKey`) and one of 21 templates (`tests/Ladder.DI.Tests/Graph/Nodes.cs`). The templates are generic classes closed at run time with `MakeGenericType`, so a random graph needs no generated code. They include: plain and one- or two-dependency constructors, `IEnumerable<T>` dependencies, an injected `IServiceProvider`, `IDisposable`, `IAsyncDisposable` and both, three-constructor classes where the longest satisfiable one wins, two-constructor classes that are ambiguous when both are satisfiable, `[FromKeyedServices("k1")]`, `[FromKeyedServices]` (inherit the key), `[ServiceKey] object` and `[ServiceKey] int` (wrong type for string keys), optional parameters, a constructor that throws after its dependency was created, factories (keyed and unkeyed), instances, and three open generics: plain, disposable and constrained (`where T : IMarked`, which `K1`, `K3` and `K5` aren't). Disposable templates are drawn three times as often, because disposal order is half the claim.
- **Dependencies** are drawn per registration. In *acyclic* scenarios (half) every dependency points to a lower-numbered service, and factories resolve their dependency through the provider. In *cyclic* scenarios dependencies point anywhere, so cycles must be detected at planning time, and factories resolve nothing: a cycle through a factory is invisible to planning in every container and ends in a stack overflow, which would kill the test host rather than be an observation.
- **Operations**: `GetService`, `GetKeyedService` (including `AnyKey` and the unregistered key `"k3"`), `GetServices`, `GetKeyedServices`, `GetRequiredService`, `CreateScope` from any live or disposed scope, `Dispose` and `DisposeAsync` of a scope. The container itself is disposed once, at the end.

## What is compared

Each scenario runs against both containers and writes one line per step. The two transcripts must be identical.

- **Instance identity and creation order.** Every node numbers itself from a per-run counter when it is constructed, and an object graph is printed with each node in full the first time and as `#id` after that. Equal transcripts therefore mean the same instances were shared in the same places and created in the same order. Injected providers are named by first appearance, or by the scope that owns them.
- **Disposal order.** Every `Dispose`/`DisposeAsync` call is logged (`D3`, `A7`), and the full log ends the transcript.
- **Failures.** Exception type, and for `InvalidOperationException` which rule fired, classified from the message: cycle, ambiguous constructor, captive dependency, scoped from root, unresolvable parameter, no satisfiable constructor, `AnyKey` for a single service, required service missing, `IAsyncDisposable`-only on sync dispose, `[ServiceKey]` type mismatch. `ValidateOnBuild`'s `AggregateException` is compared inner exception by inner exception. Message *wording* is carried in the transcript for diagnosis but stripped before comparing; it is not claimed.

Example transcript (a cyclic scenario; `!` marks an exception):

```
no scope to dispose
scope 1 from 0
get IS<K4> in 1: null
all IS<K1> in 1: [AsyncDisposable<K1,IS<K0>>#1(Disposable<K0,RunLog>#0(Ladder.DI.Tests.Graph.RunLog))]
scope 2 from 0
GetKeyed !InvalidOperationException:unresolvable
get IS<K1> [k3] in 3: OpenDisposable<K1>#2()
get IS<K4> [k1] in 4: Longest<K4,IEnumerable<IS<K0>>,IS<K3>>#4('A',[AsyncDisposable<K0,RunLog>#3(Ladder.DI.Tests.Graph.RunLog),#0])
all IS<K1> in 2: [#1]
GetRequired !ObjectDisposedException
...
root dispose !InvalidOperationException:async-only
disposals D2,D5
```

## Result

**10,000 scenarios per run, 0 disagreements.** The test asserts the generator reached each region below in a minimum number of scenarios, so agreement isn't agreement about nothing. Counts from the final run (`dotnet test -c Release`, random seed), as the number of scenarios whose transcript contained each event:

| Event | Scenarios | Event | Scenarios |
|---|---:|---|---:|
| an instance returned again (`#id`) | 1,497 | cycle refused | 660 |
| ≥ 3 disposals in the final log | 1,313 | ambiguous constructor | 325 |
| use after dispose (`ObjectDisposedException`) | 3,473 | captive dependency (ValidateScopes) | 126 |
| build refused (ValidateOnBuild) | 1,555 | scoped service from root (ValidateScopes) | 670 |
| constructor threw mid-graph (`NotSupportedException`) | 303 | `[ServiceKey]` type mismatch | 448 |
| open generic constraint violated (`ArgumentException`) | 544 | `IAsyncDisposable`-only on sync dispose | 428 |
| `AnyKey` for a single service | 2,711 | unresolvable parameter | 2,456 |
| no satisfiable constructor | 159 | required service missing | 3,747 |

The final container and generator were run eight times at 10,000 scenarios on fresh random seeds (80,000 scenarios, this one included), all agreeing. A failure prints a CsCheck seed (`CsCheck_Seed=… dotnet test …` reproduces it) and both full transcripts, with messages.

## What it found

The first three runs failed. Two were real differences between the containers; one was the harness.

1. **Microsoft's container keys instances by (service, slot), not by registration.** We first keyed each instance by the registration that produced it, which looks equivalent and passes all 143 conformance tests. It isn't equivalent. A single resolution reads slot 0. Resolving `IEnumerable<IS<K1>>` assigns slots to the open generics that fit `K1` and skips the constrained one that doesn't. So if `IS<K1>` is resolved *after* the enumeration, it returns the open generic the enumeration put in slot 0 (`Open<K1>`). Resolved *before*, it tries the last open generic registration, `OpenConstrained<>`, and throws `ArgumentException`. The planner now keys plans exactly as `CallSiteFactory` does (`Planner.cs`, `InstanceKey`). `ContainerTests.An_enumeration_can_decide_a_later_single_resolution` pins it against both containers.
2. **Microsoft's scope validator gives a false "captive dependency" for keyed instances.** `CallSiteValidator` memoizes by `ResultCache.Key`. A `ConstantCallSite` (any instance registration, keyed or not) has the key `(unkeyed service type, slot 0)`, the same as the unkeyed slot-0 registration. If that registration is scoped and has been validated, a singleton that depends on a *keyed instance* of the same type is refused as capturing a scoped service. Ours memoizes by plan, so it accepted the build Microsoft refused. We now key the memo the same way (`ScopeValidator.cs`). `ContainerTests.Scope_validation_mistakes_a_keyed_instance_for_the_scoped_registration_once_that_was_validated` pins it against both.
3. **The harness classified `ObjectDisposedException` as an `InvalidOperationException` rule**, because it derives from it. This was fixed in the test. A related finding: the per-kind classification relies on the two containers sharing exception wording. They do, except that Microsoft writes C#-style type names (`IEnumerable<IS<K3>>`) in its circular-dependency and `IAsyncDisposable` messages. Ours now does too (`TypeNames.cs`).

A third behaviour was checked rather than found: an `AnyKey` enumeration leaves out keyed open generics. It resolves each registration under its own (key, service type), and for an open generic that type is the open definition, which can't be closed. Both containers agree (`ContainerTests.An_AnyKey_enumeration_leaves_out_keyed_open_generics`).

## Not covered

- Concurrency. Scenarios are single-threaded. Concurrent first resolution is tested separately (`Concurrent_first_resolutions_share_one_singleton_and_one_scoped_instance_per_scope`), against ours only.
- Cycles through factories (see above), deep graphs (Microsoft moves call-site construction to a fresh thread when the stack runs low; ours does not, see the README), value-type services, and `ActivatorUtilities`, which belongs to the abstractions package and is exercised by the spec suite.
