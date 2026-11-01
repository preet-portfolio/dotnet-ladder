<!-- Draft for Preet to review and file at https://github.com/dotnet/runtime/issues/new (area-Extensions-DependencyInjection). Not filed. -->

**Title:** `GetService` on a constrained open generic throws or succeeds depending on whether `GetServices` was called first

### Description

Two open-generic registrations exist for the same service type, and the last one has a constraint the requested type
argument does not satisfy. On these registrations, `ServiceProvider.GetService` gives different results depending on
call order:
- On a fresh provider it throws `ArgumentException`.
- If `GetServices` for the same closed type was called first on that provider, it succeeds and returns the other
  implementation.

#90158 was closed with the explanation that throwing here is intentional. This report is not asking to change that
decision. The problem is that the outcome is not deterministic: whether an application crashes depends on whether
some other code happened to enumerate the service earlier.

### Reproduction Steps

```csharp
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddTransient(typeof(IS<>), typeof(Open<>));
services.AddTransient(typeof(IS<>), typeof(Constrained<>));   // A does not satisfy Constrained<T>'s constraint

using (var provider = services.BuildServiceProvider())
    Console.WriteLine(Try(() => provider.GetService<IS<A>>()));          // ArgumentException

using (var provider = services.BuildServiceProvider())
{
    Console.WriteLine(provider.GetServices<IS<A>>().Count());           // 1
    Console.WriteLine(Try(() => provider.GetService<IS<A>>()));          // OK, Open`1
}

static string Try(Func<object?> f) { try { return "OK, " + f()?.GetType().Name; } catch (Exception e) { return e.GetType().Name; } }

public interface IMarked;
public sealed class A;
public interface IS<T>;
public sealed class Open<T> : IS<T>;
public sealed class Constrained<T> : IS<T> where T : IMarked;
```

### Expected behavior

`GetService<IS<A>>()` behaves the same way on a fresh provider and after `GetServices<IS<A>>()`. Either result is fine,
as long as it is consistent:
- always throw, which matches the decision in #90158; or
- always skip the registration whose constraint fails, which matches what `GetServices` already does.

### Actual behavior

```
GetService first:   ArgumentException: GenericArguments[0], 'A', on 'Constrained`1[T]' violates the constraint of type 'T'.
GetServices first:  1 service(s)
then GetService:    OK, Open`1
```

### Regression?

Not known. Observed with Microsoft.Extensions.DependencyInjection 10.0.14 on runtimes 10.0.13 and 10.0.14.

### Known Workarounds

Do not register a constrained open generic as the last implementation of a service type that may be requested with
type arguments that violate the constraint.

### Configuration

.NET 10.0.14, Linux x64. Not configuration-specific, as far as we know.

### Other information

Our guess at the cause [not verified against the source]:
- `GetServices` builds the enumerable call site. That build skips the implementation whose constraint fails, and it
  caches a call site for `IS<A>` along the way.
- A later `GetService` reuses that cached call site instead of trying the last registration.

Related:
- #90158 (constraint not validated; closed as intentional)
- #125926 (`IsService` unsound for constrained open generics; open)
