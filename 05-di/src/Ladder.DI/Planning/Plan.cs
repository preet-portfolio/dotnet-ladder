using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI.Planning;

/// <summary>
/// Where a plan's result lives once created. The order matters: the "most transient" of several locations is the
/// largest value, which is how an <c>IEnumerable&lt;T&gt;</c> picks where to cache itself (the same order as
/// <c>CallSiteResultCacheLocation</c>).
/// </summary>
internal enum CacheLocation
{
    /// <summary>Singleton: one instance per container, created in the root scope.</summary>
    Root = 0,

    /// <summary>Scoped: one instance per scope (and one in the root scope, if resolved there).</summary>
    Scope = 1,

    /// <summary>Transient: a new instance every time, captured for disposal by the resolving scope.</summary>
    Dispose = 2,

    /// <summary>Not cached and not disposed: instances supplied by the caller, constants, the scope itself.</summary>
    None = 3,
}

/// <summary>
/// A resolution plan: a tree that says how to produce one service. Plans are built once per service and per
/// registration, then either interpreted (<see cref="Resolution.Interpreter"/>) or compiled to a delegate
/// (<see cref="Resolution.PlanCompiler"/>).
/// </summary>
internal abstract class Plan(Type serviceType, CacheLocation location)
{
    public Type ServiceType { get; } = serviceType;

    public CacheLocation Location { get; } = location;

    /// <summary>Index into each scope's instance array, for plans cached per scope; -1 otherwise.</summary>
    public int ScopeSlot { get; init; } = -1;

    /// <summary>
    /// The root-scope instance (singletons, and scoped services resolved from the root). Written once under a lock
    /// on this plan, read without one; null means "not created yet".
    /// </summary>
    public object? RootValue;

    /// <summary>
    /// The plan's body compiled to a delegate, set when a plan cached per scope is compiled, so a compiled caller can
    /// create it through <see cref="Scope.GetOrCreateScoped"/>.
    /// </summary>
    public Func<Scope, object?>? CompiledBody;

    /// <summary>For scope validation: the first scoped service in this plan's tree, computed once.</summary>
    public Type? FirstScopedService;

    public bool ScopeChecked;

    /// <summary>
    /// Whether the produced object might need disposing. For a constructor this is known statically from the
    /// implementation type, which lets compiled code skip the capture call entirely for non-disposable transients.
    /// </summary>
    public virtual bool MayNeedDisposal => true;
}

/// <summary>A value known at planning time: a registered instance, a parameter's default value, a service key.</summary>
internal sealed class ConstantPlan(Type serviceType, object? value) : Plan(serviceType, CacheLocation.None)
{
    public object? Value { get; } = value;
}

/// <summary>The scope doing the resolving, injected as <see cref="IServiceProvider"/>.</summary>
internal sealed class ScopePlan() : Plan(typeof(IServiceProvider), CacheLocation.None);

internal sealed class ConstructorPlan(Type serviceType, CacheLocation location, ConstructorInfo constructor, Plan[] arguments)
    : Plan(serviceType, location)
{
    public ConstructorInfo Constructor { get; } = constructor;

    public Plan[] Arguments { get; } = arguments;

    public Type ImplementationType => Constructor.DeclaringType!;

    public override bool MayNeedDisposal =>
        typeof(IDisposable).IsAssignableFrom(ImplementationType) || typeof(IAsyncDisposable).IsAssignableFrom(ImplementationType);
}

/// <summary>A registered factory. Keyed factories also receive the key that was asked for.</summary>
internal sealed class FactoryPlan : Plan
{
    public FactoryPlan(Type serviceType, CacheLocation location, Func<IServiceProvider, object> factory)
        : base(serviceType, location) => Factory = factory;

    public FactoryPlan(Type serviceType, CacheLocation location, object? key, Func<IServiceProvider, object?, object> keyedFactory)
        : base(serviceType, location)
    {
        Key = key;
        KeyedFactory = keyedFactory;
    }

    public Func<IServiceProvider, object>? Factory { get; }

    public Func<IServiceProvider, object?, object>? KeyedFactory { get; }

    public object? Key { get; }
}

/// <summary><c>IEnumerable&lt;T&gt;</c>: an array of every matching registration, in registration order.</summary>
internal sealed class EnumerablePlan(Type serviceType, CacheLocation location, Type itemType, Plan[] items) : Plan(serviceType, location)
{
    public Type ItemType { get; } = itemType;

    public Plan[] Items { get; } = items;

    public override bool MayNeedDisposal => false;
}

internal static class Lifetimes
{
    public static CacheLocation ToLocation(this ServiceLifetime lifetime) => lifetime switch
    {
        ServiceLifetime.Singleton => CacheLocation.Root,
        ServiceLifetime.Scoped => CacheLocation.Scope,
        ServiceLifetime.Transient => CacheLocation.Dispose,
        _ => CacheLocation.None,
    };
}
