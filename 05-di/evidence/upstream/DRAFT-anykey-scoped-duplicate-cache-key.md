<!-- Draft for Preet to review and file at https://github.com/dotnet/runtime/issues/new (area-Extensions-DependencyInjection). Not filed. -->

**Title:** Scoped `AnyKey` service that depends on a keyed `IEnumerable` of its own type throws `ArgumentException` ("An item with the same key has already been added. Key: ServiceCacheKey")

### Description

Take two registrations of one service type:
- a **closed** registration under `KeyedService.AnyKey`, which takes `[FromKeyedServices("k1")] IEnumerable<IS<T>>`;
- an **open generic** registration under `"k1"`.

Resolving the service with key `"k1"` from a scope picks the `AnyKey` registration, which is expected: a closed
registration under `AnyKey` beats the open generic under the exact key. Its dependency, the `"k1"` enumerable, holds
only the open generic, which is also expected, because `AnyKey` registrations are not enumerated.

When both are **scoped**, the resolution throws an `ArgumentException` from an internal dictionary in
`ServiceProviderEngineScope`. When both are transient or singleton, the same resolution succeeds. It also succeeds if
the `"k1"` enumerable was resolved earlier in the scope, but then it returns the open generic instead of the `AnyKey`
service.

From reading the source, the outer service and the enumerable's only item get the same `ServiceCacheKey`, with the
same service identifier `(IS<T>, "k1")` and slot 0. The item is cached first, while the outer service's dependencies
are being built. Then caching the outer service fails with a duplicate key.

### Reproduction Steps

```csharp
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddKeyedScoped(typeof(IS<K>), KeyedService.AnyKey, typeof(KeyedDep<K>));
services.AddKeyedScoped(typeof(IS<>), "k1", typeof(Open<>));

using var root = services.BuildServiceProvider();
using var scope = root.CreateScope();
scope.ServiceProvider.GetKeyedService<IS<K>>("k1");   // ArgumentException

public interface IS<T>;
public sealed class K;
public sealed class Open<T> : IS<T>;
public sealed class KeyedDep<T>([FromKeyedServices("k1")] IEnumerable<IS<T>> all) : IS<T>;
```

The full repro has 10 variants, each changing one ingredient:
[`keyed-enumerable-duplicate-key/Program.cs`](keyed-enumerable-duplicate-key/Program.cs).

### Expected behavior

`KeyedDep<K>` holding `[Open<K>]`, which is what transient and singleton lifetimes return on a fresh provider.

### Actual behavior

Output on Microsoft.Extensions.DependencyInjection 10.0.14 (runtime 10.0.14). It is identical on **11.0.0-rc.3.26522.148**:

```
1. scoped AnyKey closed + scoped "k1" open generic: ArgumentException: An item with the same key has already been added. Key: Microsoft.Extensions.DependencyInjection.ServiceLookup.ServiceCacheKey
2. same, transient: KeyedDep([Open<K>])
3. same, singleton: KeyedDep([Open<K>])
4. "k1" registration closed instead of open generic: Open<K>
5. dependency is IS<K> "k1" (not IEnumerable): InvalidOperationException: A circular dependency was detected for the service of type 'IS<K>'.
IS<K>(SingleDep<K>) -> IS<K>
6. resolve IEnumerable<IS<K>> "k1" directly: Open<K>
7. resolve with key "k2" (served only by AnyKey): KeyedDep([Open<K>])
8. singleton: resolve the service, then the "k1" enumerable: KeyedDep([Open<K>]) then [Open<K>]
9. scoped: resolve the "k1" enumerable, then the service: [Open<K>] then Open<K>
10. singleton: resolve the "k1" enumerable, then the service: [Open<K>] then Open<K>
```

Cases 5 and 9/10 matter for a fix:
- **Case 5:** with a single `IS<K>` dependency instead of the enumerable, the same graph is reported as a circular
  dependency. So the resolver itself treats the `AnyKey` service and the `"k1"` lookup as the same service.
- **Cases 9 and 10:** once the enumerable has been resolved, a single resolution returns the open generic. The result
  depends on call order. This is the same (service, slot) sharing that makes the last registration and the last
  enumerable item one instance.

### Regression?

Not known. Observed on 10.0.13, 10.0.14 and 11.0.0-rc.3.

### Known Workarounds

Register the `"k1"` implementation as a closed type (case 4), or avoid the scoped lifetime for this pair.

### Configuration

Linux x64; Microsoft.Extensions.DependencyInjection 10.0.14 and 11.0.0-rc.3.26522.148; runtime 10.0.14.

### Other information

Found by a differential test that runs random service graphs through Microsoft's container and an independent
implementation. It shows up in about 1 of 200,000 random graphs.
