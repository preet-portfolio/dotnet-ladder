# Upstream reports for Microsoft.Extensions.DependencyInjection (drafts, not filed)

These are behaviours of Microsoft's container that we found while building ours. Each has:
- a minimal repro;
- a run of that repro against the newest 10.0.x packages, `Microsoft.Extensions.DependencyInjection` **10.0.14** on
  runtime 10.0.14;
- a search of the dotnet/runtime tracker.

**Nothing here has been filed.** Filing is Preet's decision.

Run a repro with `dotnet run -c Release --project <dir>`. Each directory's `output-10.0.14.txt` is the output it produced on
runtime 10.0.14.

## (a) Scope validation confuses a keyed singleton instance with an unkeyed scoped registration

- **Repro:** [`scope-validation-keyed-instance/`](scope-validation-keyed-instance/).
- **Registrations:**
  - `AddScoped<IFoo, Foo>()`
  - `AddKeyedSingleton<IFoo>("k", new Foo())`
  - a singleton `Consumer` that takes `[FromKeyedServices("k")] IFoo`

  `Consumer` depends only on the keyed singleton.
- **Result on 10.0.14:**
  - A fresh provider resolves `Consumer` without complaint.
  - After the scoped `IFoo` has been resolved once, resolving `Consumer` throws "Cannot consume scoped service 'IFoo'
    from singleton 'Consumer'".
  - `ValidateOnBuild` fails with the same error.
  - So the outcome depends on what was resolved earlier.
- **Already reported and fixed:**
  - Issue [dotnet/runtime#126960](https://github.com/dotnet/runtime/issues/126960).
  - The fix is PR [dotnet/runtime#127051](https://github.com/dotnet/runtime/pull/127051), merged 2026-04-22 with
    milestone 11.0.0. It passes the service key through `ResultCache.None` and `ConstantCallSite`.
  - The fix is not in 10.0.14; the repro above still fails there.
- **No draft needed.** If anything, the only open question for upstream is whether the fix should be backported to
  10.0.x. Preet can ask that on #126960.

## (b) `GetService` for a constrained open generic depends on whether `GetServices` ran first

- **Repro:** [`constrained-open-generic-order/`](constrained-open-generic-order/).
- **Registrations:** `IS<>` → `Open<>`, then `IS<>` → `Constrained<T> where T : IMarked`. The type `A` does not
  implement `IMarked`.
- **Result on 10.0.14:**
  - On a fresh provider, `GetService<IS<A>>()` throws `ArgumentException` ("GenericArguments[0], 'A', on
    'Constrained`1[T]' violates the constraint of type 'T'").
  - On another fresh provider, `GetServices<IS<A>>()` returns 1 service, and `GetService<IS<A>>()` called *after* it
    succeeds and returns `Open<A>`.
  - The same call on the same registrations either throws or succeeds, depending on call order.
- **Search** (`is:issue repo:dotnet/runtime` with "open generic" constraint, GetService GetServices constraint,
  TryCreateOpenGeneric):
  - [#90158](https://github.com/dotnet/runtime/issues/90158), "doesn't validate generic constraint": `GetService` throws
    `ArgumentException` from `TryCreateOpenGeneric`. Closed as not planned on 2026-02-12, with the reply that the
    current behaviour is intentional.
  - [#125926](https://github.com/dotnet/runtime/issues/125926): `IsService` is unsound for constrained open generics.
    Open, milestone Future.
  - Neither one mentions the **order dependence**. #90158 accepts that `GetService` throws. What it does not cover is
    that the same call stops throwing once `GetServices` has filled the call-site cache.
- **Draft:** [`DRAFT-constrained-open-generic-order.md`](DRAFT-constrained-open-generic-order.md). It is written so that
  either answer is useful: "throw consistently" or "skip consistently".
