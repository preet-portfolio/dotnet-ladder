using System.Collections.Concurrent;
using Ladder.DI.Planning;
using Ladder.DI.Resolution;
using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI;

/// <summary>How warm resolutions run.</summary>
public enum ResolutionMode
{
    /// <summary>Interpret the first resolution of each service, compile it to a delegate on the second (the default).</summary>
    Compiled,

    /// <summary>Compile before the first resolution. Same behaviour, every call compiled; used to run the conformance suite through the compiler.</summary>
    CompiledOnFirstCall,

    /// <summary>Always walk the plan with reflection. Kept for comparison and for platforms without dynamic code.</summary>
    Interpreted,
}

public sealed class ServiceContainerOptions
{
    /// <summary>Refuse singletons that capture scoped services, and scoped services resolved from the root.</summary>
    public bool ValidateScopes { get; init; }

    /// <summary>Plan every registration when the container is built, so missing dependencies and cycles fail early.</summary>
    public bool ValidateOnBuild { get; init; }

    public ResolutionMode Mode { get; init; } = ResolutionMode.Compiled;
}

/// <summary>
/// A dependency injection container built from an <see cref="IServiceCollection"/>. Resolving goes:
/// accessor cache (one dictionary probe) → accessor delegate. Building an accessor goes: <see cref="Planner"/> builds
/// the plan (cycles and unresolvable constructors fail here), the plan is validated if asked, and the accessor
/// interprets it once and then compiles it. A singleton's accessor is just its value.
/// </summary>
public sealed class ServiceContainer : IKeyedServiceProvider, IDisposable, IAsyncDisposable
{
    private readonly ServiceContainerOptions _options;
    private readonly ScopeValidator? _validator;

    // Unkeyed lookups are the hot path, so they get a dictionary keyed by Type alone: no struct key to hash and compare.
    private readonly ConcurrentDictionary<Type, Accessor> _unkeyed = new();
    private readonly ConcurrentDictionary<ServiceId, Accessor> _keyed = new();
    private readonly Func<Type, Accessor> _createUnkeyed;
    private readonly Func<ServiceId, Accessor> _createKeyed;
    private volatile bool _disposed;

    public ServiceContainer(IServiceCollection services)
        : this(services, new ServiceContainerOptions())
    {
    }

    public ServiceContainer(IServiceCollection services, ServiceContainerOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _validator = options.ValidateScopes ? new ScopeValidator() : null;
        _createUnkeyed = type => CreateAccessor(ServiceId.Of(type));
        _createKeyed = CreateAccessor;
        Root = new Scope(this, isRoot: true);
        Planner = new Planner(services);

        // Built-in services. IServiceProvider is whichever scope is resolving; the rest are container-wide.
        Planner.AddBuiltIn(typeof(IServiceProvider), new ScopePlan());
        Planner.AddBuiltIn(typeof(IServiceScopeFactory), new ConstantPlan(typeof(IServiceScopeFactory), Root));
        Planner.AddBuiltIn(typeof(IServiceProviderIsService), new ConstantPlan(typeof(IServiceProviderIsService), Planner));
        Planner.AddBuiltIn(typeof(IServiceProviderIsKeyedService), new ConstantPlan(typeof(IServiceProviderIsKeyedService), Planner));

        if (options.ValidateOnBuild)
        {
            ValidateAll();
        }
    }

    internal Planner Planner { get; }

    internal Scope Root { get; }

    public object? GetService(Type serviceType) => Resolve(serviceType, Root);

    public object? GetKeyedService(Type serviceType, object? serviceKey) => ResolveKeyed(serviceType, serviceKey, Root);

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey) => ResolveRequiredKeyed(serviceType, serviceKey, Root);

    public void Dispose() => Root.Dispose();

    public ValueTask DisposeAsync() => Root.DisposeAsync();

    internal IServiceScope CreateScope()
    {
        if (_disposed)
        {
            Errors.ThrowDisposed();
        }

        return new Scope(this, isRoot: false);
    }

    internal void MarkDisposed() => _disposed = true;

    internal object? Resolve(Type serviceType, Scope scope)
    {
        if (_disposed)
        {
            Errors.ThrowDisposed();
        }

        if (!_unkeyed.TryGetValue(serviceType, out Accessor? accessor))
        {
            accessor = _unkeyed.GetOrAdd(serviceType, _createUnkeyed);
        }

        return Invoke(accessor, scope);
    }

    internal object? ResolveKeyed(Type serviceType, object? serviceKey, Scope scope)
    {
        if (serviceKey is null)
        {
            return Resolve(serviceType, scope); // a null key is the unkeyed service
        }

        // AnyKey can enumerate (IEnumerable<T>) but never pick one service.
        if (ReferenceEquals(serviceKey, KeyedService.AnyKey)
            && !(serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IEnumerable<>)))
        {
            Errors.ThrowAnyKeyForSingleService();
        }

        if (_disposed)
        {
            Errors.ThrowDisposed();
        }

        var id = new ServiceId(serviceKey, serviceType);
        if (!_keyed.TryGetValue(id, out Accessor? accessor))
        {
            accessor = _keyed.GetOrAdd(id, _createKeyed);
        }

        return Invoke(accessor, scope);
    }

    internal object ResolveRequiredKeyed(Type serviceType, object? serviceKey, Scope scope)
    {
        object? service = ResolveKeyed(serviceType, serviceKey, scope);
        if (service is null)
        {
            if (serviceKey is null)
            {
                Errors.ThrowNoService(serviceType);
            }

            Errors.ThrowNoKeyedService(serviceType, serviceKey.GetType());
        }

        return service;
    }

    private object? Invoke(Accessor accessor, Scope scope)
    {
        if (_validator is not null && accessor.Plan is { } plan)
        {
            _validator.ValidateResolution(plan, scope);
        }

        return accessor.Resolver(scope);
    }

    private Accessor CreateAccessor(ServiceId id)
    {
        Plan? plan = Planner.GetPlan(id);
        if (plan is null)
        {
            return Accessor.Missing;
        }

        _validator?.Validate(plan);

        if (plan.Location == CacheLocation.Root)
        {
            // A singleton never changes once created, so its accessor is its value: nothing to interpret or compile.
            object? value = Interpreter.ResolveRoot(plan, Root);
            return new Accessor(plan, _ => value);
        }

        return _options.Mode switch
        {
            ResolutionMode.Interpreted => new Accessor(plan, scope => Interpreter.Resolve(plan, scope)),
            ResolutionMode.CompiledOnFirstCall => new Accessor(plan, PlanCompiler.Compile(plan, Root)),
            _ => Accessor.CompileOnSecondCall(plan, Root),
        };
    }

    private void ValidateAll()
    {
        var errors = new List<Exception>();
        for (int i = 0; i < Planner.Descriptors.Count; i++)
        {
            ServiceDescriptor descriptor = Planner.Descriptors[i];
            if (descriptor.ServiceType.IsGenericTypeDefinition)
            {
                continue; // open generics can only be checked once closed
            }

            try
            {
                Plan plan = Planner.GetPlan(i);
                _validator?.Validate(plan);
            }
            catch (Exception e)
            {
                errors.Add(new InvalidOperationException($"Error while validating the service descriptor '{descriptor}': {e.Message}", e));
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregateException("Some services are not able to be constructed", errors);
        }
    }

    private sealed class Accessor(Plan? plan, Func<Scope, object?> resolver)
    {
        public static readonly Accessor Missing = new(null, _ => null);

        private int _calls;

        public Plan? Plan { get; } = plan;

        public Func<Scope, object?> Resolver { get; private set; } = resolver;

        /// <summary>
        /// Interprets, and on the second call compiles. One-shot services never pay for compilation. Unlike
        /// Microsoft's container, which compiles on a thread-pool thread, this compiles inline: the second caller
        /// pays once, and there is no window where two engines run the same service.
        /// </summary>
        public static Accessor CompileOnSecondCall(Plan plan, Scope root)
        {
            var accessor = new Accessor(plan, null!);
            accessor.Resolver = scope =>
            {
                object? result = Interpreter.Resolve(plan, scope);
                if (Interlocked.Increment(ref accessor._calls) == 2)
                {
                    accessor.Resolver = PlanCompiler.Compile(plan, root);
                }

                return result;
            };
            return accessor;
        }
    }
}
