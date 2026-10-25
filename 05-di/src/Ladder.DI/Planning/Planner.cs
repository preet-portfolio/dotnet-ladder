using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI.Planning;

/// <summary>
/// Turns registrations into resolution plans. Every rule here follows <c>ServiceLookup/CallSiteFactory.cs</c> in
/// dotnet/runtime release/10.0, because the specification suite and the differential tests hold us to its behaviour:
/// <list type="bullet">
/// <item>the last registration wins for a single service; <c>IEnumerable&lt;T&gt;</c> returns all, in registration order;</item>
/// <item>an exact (closed) registration beats an open generic one; only the last open generic is tried, and its
/// constraint violation throws, while <c>IEnumerable&lt;T&gt;</c> silently skips open generics whose constraints fail;</item>
/// <item>constructors are tried longest first; the first one whose every parameter is resolvable (or has a default)
/// wins, and any other satisfiable constructor must take a subset of its parameter types, or it is ambiguous;</item>
/// <item><c>KeyedService.AnyKey</c> registrations answer any non-null key for a single service but never appear in
/// enumerations, and an <c>AnyKey</c> enumeration lists every keyed registration but not the <c>AnyKey</c> ones.</item>
/// </list>
/// Plans are built under one lock. That is only paid the first time each service is asked for; after that the
/// container serves it from its accessor cache without coming here.
/// </summary>
internal sealed class Planner : IServiceProviderIsKeyedService
{
    private readonly ServiceDescriptor[] _descriptors;
    private readonly Dictionary<ServiceId, int[]> _byId;
    private readonly Dictionary<ServiceId, Plan> _plans = [];
    private readonly Dictionary<InstanceKey, Plan> _instances = [];
    private readonly Lock _sync = new();
    private int _scopeSlots;

    public Planner(ICollection<ServiceDescriptor> descriptors)
    {
        // Copy: the caller may keep mutating its collection (the spec suite reverses it and builds a second provider).
        _descriptors = new ServiceDescriptor[descriptors.Count];
        descriptors.CopyTo(_descriptors, 0);

        var byId = new Dictionary<ServiceId, List<int>>();
        for (int i = 0; i < _descriptors.Length; i++)
        {
            ServiceDescriptor descriptor = _descriptors[i];
            Validate(descriptor, nameof(descriptors));
            ServiceId id = ServiceId.Of(descriptor);
            if (!byId.TryGetValue(id, out List<int>? indices))
            {
                byId[id] = indices = [];
            }

            indices.Add(i);
        }

        _byId = byId.ToDictionary(entry => entry.Key, entry => entry.Value.ToArray());
    }

    public IReadOnlyList<ServiceDescriptor> Descriptors => _descriptors;

    /// <summary>How many per-scope instance slots plans have claimed so far; scopes size their arrays from it.</summary>
    public int ScopeSlotCount => Volatile.Read(ref _scopeSlots);

    /// <summary>Services the container supplies itself. Like Microsoft's, they take precedence over registrations of the same type.</summary>
    public void AddBuiltIn(Type type, Plan plan)
    {
        lock (_sync)
        {
            _plans[ServiceId.Of(type)] = plan;
        }
    }

    public Plan? GetPlan(ServiceId id) => GetPlan(id, new ResolutionChain());

    /// <summary>The plan for one specific registration, as <c>ValidateOnBuild</c> checks every descriptor.</summary>
    public Plan GetPlan(int descriptorIndex)
    {
        lock (_sync)
        {
            return CreateExact(descriptorIndex, ServiceId.Of(_descriptors[descriptorIndex]), new ResolutionChain());
        }
    }

    public bool IsService(Type serviceType) => IsService(new ServiceId(null, serviceType));

    public bool IsKeyedService(Type serviceType, object? serviceKey) => IsService(new ServiceId(serviceKey, serviceType));

    private bool IsService(ServiceId id)
    {
        ArgumentNullException.ThrowIfNull(id.Type);
        Type type = id.Type;
        if (type.IsGenericTypeDefinition)
        {
            return false; // an open generic can't be resolved, only its closed forms
        }

        if (_byId.ContainsKey(id) || (id.Key is not null && _byId.ContainsKey(new ServiceId(KeyedService.AnyKey, type))))
        {
            return true;
        }

        if (type.IsConstructedGenericType)
        {
            // IEnumerable<T> is always resolvable (possibly empty), even though nobody registers it.
            return type.GetGenericTypeDefinition() == typeof(IEnumerable<>) || _byId.ContainsKey(id.GenericDefinition());
        }

        return type == typeof(IServiceProvider) || type == typeof(IServiceScopeFactory)
            || type == typeof(IServiceProviderIsService) || type == typeof(IServiceProviderIsKeyedService);
    }

    private Plan? GetPlan(ServiceId id, ResolutionChain chain)
    {
        lock (_sync)
        {
            if (_plans.TryGetValue(id, out Plan? plan))
            {
                return plan;
            }

            chain.CheckCircularDependency(id);
            plan = TryExact(id, chain) ?? (Plan?)TryOpenGeneric(id, chain) ?? TryEnumerable(id, chain);
            if (plan is not null)
            {
                _plans[id] = plan;
            }

            return plan;
        }
    }

    private Plan? TryExact(ServiceId id, ResolutionChain chain)
    {
        if (_byId.TryGetValue(id, out int[]? indices)
            || (id.Key is not null && _byId.TryGetValue(new ServiceId(KeyedService.AnyKey, id.Type), out indices)))
        {
            // For an AnyKey registration the instance is keyed by the key actually asked for, so each key gets its own singleton.
            return CreateExact(indices[^1], id, chain);
        }

        return null;
    }

    private ConstructorPlan? TryOpenGeneric(ServiceId id, ResolutionChain chain)
    {
        if (!id.Type.IsConstructedGenericType)
        {
            return null;
        }

        ServiceId definition = id.GenericDefinition();
        if (_byId.TryGetValue(definition, out int[]? indices)
            || (id.Key is not null && _byId.TryGetValue(new ServiceId(KeyedService.AnyKey, definition.Type), out indices)))
        {
            return CreateOpenGeneric(indices[^1], id, chain, throwOnConstraintViolation: true);
        }

        return null;
    }

    private EnumerablePlan? TryEnumerable(ServiceId id, ResolutionChain chain)
    {
        if (!id.Type.IsConstructedGenericType || id.Type.GetGenericTypeDefinition() != typeof(IEnumerable<>))
        {
            return null;
        }

        chain.Add(id);
        try
        {
            Type itemType = id.Type.GenericTypeArguments[0];
            var itemId = new ServiceId(id.Key, itemType);
            bool anyKey = ReferenceEquals(id.Key, KeyedService.AnyKey);
            var items = new List<(int Index, Plan Plan)>();

            if (!itemType.IsConstructedGenericType && !anyKey && _byId.TryGetValue(itemId, out int[]? indices))
            {
                foreach (int i in indices)
                {
                    items.Add((i, CreateExact(i, itemId, chain)));
                }
            }
            else
            {
                // Exact matches first, then open generics, each walked last-to-first as CallSiteFactory does (it
                // matters only for which error surfaces first when several registrations are broken).
                for (int i = _descriptors.Length - 1; i >= 0; i--)
                {
                    ServiceDescriptor d = _descriptors[i];
                    if (KeysMatch(itemId.Key, d.ServiceKey) && d.ServiceType == itemType)
                    {
                        // An AnyKey enumeration resolves each registration under its own key, so its instances are
                        // the ones GetKeyedService(thatKey) returns.
                        items.Add((i, CreateExact(i, anyKey ? ServiceId.Of(d) : itemId, chain)));
                    }
                }

                for (int i = _descriptors.Length - 1; i >= 0; i--)
                {
                    ServiceDescriptor d = _descriptors[i];
                    if (KeysMatch(itemId.Key, d.ServiceKey) && itemType.IsConstructedGenericType && itemType.GetGenericTypeDefinition() == d.ServiceType
                        && CreateOpenGeneric(i, anyKey ? ServiceId.Of(d) : itemId, chain, throwOnConstraintViolation: false) is { } plan)
                    {
                        items.Add((i, plan));
                    }
                }

                items.Sort((a, b) => a.Index.CompareTo(b.Index));
            }

            // The array itself is cached as long-lived as its shortest-lived element allows: all singletons means
            // one array for the container's life, any scoped element means one per scope, anything else a new one
            // per resolution. Instances (constants) count as uncached, as in Microsoft's container.
            var location = CacheLocation.Root;
            foreach ((_, Plan plan) in items)
            {
                location = (CacheLocation)Math.Max((int)location, (int)plan.Location);
            }

            if (location is not (CacheLocation.Root or CacheLocation.Scope))
            {
                location = CacheLocation.None;
            }

            return new EnumerablePlan(id.Type, location, itemType, [.. items.Select(item => item.Plan)]) { ScopeSlot = ClaimSlot(location) };
        }
        finally
        {
            chain.Remove(id);
        }

        static bool KeysMatch(object? lookupKey, object? descriptorKey)
        {
            if (lookupKey is null || descriptorKey is null)
            {
                return lookupKey is null && descriptorKey is null;
            }

            // AnyKey registrations are never enumerated; an AnyKey lookup matches every other key.
            return !descriptorKey.Equals(KeyedService.AnyKey)
                && (lookupKey.Equals(descriptorKey) || lookupKey.Equals(KeyedService.AnyKey));
        }
    }

    private Plan CreateExact(int index, ServiceId id, ResolutionChain chain)
    {
        var instanceKey = new InstanceKey(index, id);
        if (_instances.TryGetValue(instanceKey, out Plan? plan))
        {
            return plan;
        }

        ServiceDescriptor d = _descriptors[index];
        CacheLocation location = d.Lifetime.ToLocation();
        if (ImplementationInstance(d) is { } instance)
        {
            // Supplied by the caller: never cached per scope, never disposed by the container.
            plan = new ConstantPlan(d.ServiceType, instance);
        }
        else if (!d.IsKeyedService && d.ImplementationFactory is { } factory)
        {
            plan = new FactoryPlan(d.ServiceType, location, factory) { ScopeSlot = ClaimSlot(location) };
        }
        else if (d.IsKeyedService && d.KeyedImplementationFactory is { } keyedFactory)
        {
            plan = new FactoryPlan(d.ServiceType, location, id.Key, keyedFactory) { ScopeSlot = ClaimSlot(location) };
        }
        else if (ImplementationType(d) is { } implementation)
        {
            plan = CreateConstructorPlan(location, id, implementation, chain);
        }
        else
        {
            throw Errors.InvalidDescriptor();
        }

        _instances[instanceKey] = plan;
        return plan;
    }

    private ConstructorPlan? CreateOpenGeneric(int index, ServiceId id, ResolutionChain chain, bool throwOnConstraintViolation)
    {
        var instanceKey = new InstanceKey(index, id);
        if (_instances.TryGetValue(instanceKey, out Plan? cached))
        {
            return (ConstructorPlan)cached;
        }

        ServiceDescriptor d = _descriptors[index];
        Type closed;
        try
        {
            closed = ImplementationType(d)!.MakeGenericType(id.Type.GenericTypeArguments);
        }
        catch (ArgumentException) when (!throwOnConstraintViolation)
        {
            return null; // the type arguments violate the implementation's constraints
        }

        ConstructorPlan plan = CreateConstructorPlan(d.Lifetime.ToLocation(), id, closed, chain);
        _instances[instanceKey] = plan;
        return plan;
    }

    private ConstructorPlan CreateConstructorPlan(CacheLocation location, ServiceId id, Type implementation, ResolutionChain chain)
    {
        chain.Add(id, implementation);
        try
        {
            ConstructorInfo[] constructors = implementation.GetConstructors();
            if (constructors.Length == 0)
            {
                throw Errors.NoConstructorMatch(implementation);
            }

            if (constructors.Length == 1)
            {
                ParameterInfo[] parameters = constructors[0].GetParameters();
                Plan[] arguments = parameters.Length == 0 ? [] : CreateArguments(id, implementation, chain, parameters, throwIfMissing: true)!;
                return new ConstructorPlan(id.Type, location, constructors[0], arguments) { ScopeSlot = ClaimSlot(location) };
            }

            // Longest first. Array.Sort with this comparison on GetConstructors()' order is what CallSiteFactory
            // does, so ties between equal-length constructors break the same way.
            Array.Sort(constructors, (a, b) => b.GetParameters().Length.CompareTo(a.GetParameters().Length));

            ConstructorInfo? best = null;
            Plan[]? bestArguments = null;
            HashSet<Type>? bestParameterTypes = null;
            foreach (ConstructorInfo constructor in constructors)
            {
                ParameterInfo[] parameters = constructor.GetParameters();
                if (CreateArguments(id, implementation, chain, parameters, throwIfMissing: false) is not { } arguments)
                {
                    continue;
                }

                if (best is null)
                {
                    best = constructor;
                    bestArguments = arguments;
                    continue;
                }

                // A shorter satisfiable constructor is fine only if the longest one already covers its parameters.
                bestParameterTypes ??= [.. best.GetParameters().Select(p => p.ParameterType)];
                if (parameters.Any(p => !bestParameterTypes.Contains(p.ParameterType)))
                {
                    throw Errors.Ambiguous(implementation, best, constructor);
                }
            }

            return best is null
                ? throw Errors.UnableToActivate(implementation)
                : new ConstructorPlan(id.Type, location, best, bestArguments!) { ScopeSlot = ClaimSlot(location) };
        }
        finally
        {
            chain.Remove(id);
        }
    }

    /// <returns>Null when a parameter can't be satisfied and <paramref name="throwIfMissing"/> is false.</returns>
    private Plan[]? CreateArguments(ServiceId id, Type implementation, ResolutionChain chain, ParameterInfo[] parameters, bool throwIfMissing)
    {
        var arguments = new Plan[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            Plan? argument = null;
            bool keyed = false;
            Type parameterType = parameters[i].ParameterType;
            foreach (object attribute in parameters[i].GetCustomAttributes(true))
            {
                // [ServiceKey] receives the key this service was resolved with; it is ignored for unkeyed resolutions.
                if (id.Key is not null && attribute is ServiceKeyAttribute)
                {
                    if (ReferenceEquals(id.Key, KeyedService.AnyKey))
                    {
                        parameterType = typeof(object);
                    }
                    else if (parameterType != id.Key.GetType() && parameterType != typeof(object))
                    {
                        throw Errors.InvalidServiceKeyType();
                    }

                    argument = new ConstantPlan(parameterType, id.Key);
                    break;
                }

                if (attribute is FromKeyedServicesAttribute fromKeyed)
                {
                    object? key = fromKeyed.LookupMode switch
                    {
                        ServiceKeyLookupMode.InheritKey => id.Key,
                        ServiceKeyLookupMode.ExplicitKey => fromKeyed.Key,
                        _ => null,
                    };

                    // A null key (NullKey mode, or InheritKey inside an unkeyed service) means an ordinary lookup.
                    if (key is not null)
                    {
                        argument = GetPlan(new ServiceId(key, parameterType), chain);
                        keyed = true;
                        break;
                    }
                }
            }

            if (!keyed)
            {
                argument ??= GetPlan(ServiceId.Of(parameterType), chain);
            }

            if (argument is null && TryGetDefaultValue(parameters[i], out object? defaultValue))
            {
                argument = new ConstantPlan(parameterType, defaultValue);
            }

            if (argument is null)
            {
                return throwIfMissing ? throw Errors.CannotResolve(parameterType, implementation) : null;
            }

            arguments[i] = argument;
        }

        return arguments;
    }

    /// <summary>
    /// A parameter's default value as the container passes it (<c>Common/src/Extensions/ParameterDefaultValue</c>):
    /// a struct parameter declared <c>= default</c> or <c>= new S()</c> reports a null DefaultValue, so it gets a zeroed
    /// instance, and a nullable enum's default comes back as its underlying integer, so it is converted back.
    /// </summary>
    private static bool TryGetDefaultValue(ParameterInfo parameter, out object? value)
    {
        value = null;
        if (!parameter.HasDefaultValue)
        {
            return false;
        }

        value = parameter.DefaultValue;
        Type type = parameter.ParameterType;
        Type? underlying = Nullable.GetUnderlyingType(type);
        if (value is null && type.IsValueType && underlying is null)
        {
            value = RuntimeHelpers.GetUninitializedObject(type);
        }

        if (value is not null && underlying is { IsEnum: true })
        {
            value = Enum.ToObject(underlying, value);
        }

        return true;
    }

    private int ClaimSlot(CacheLocation location) => location == CacheLocation.Scope ? Interlocked.Increment(ref _scopeSlots) - 1 : -1;

    private static object? ImplementationInstance(ServiceDescriptor d) => d.IsKeyedService ? d.KeyedImplementationInstance : d.ImplementationInstance;

    private static Type? ImplementationType(ServiceDescriptor d) => d.IsKeyedService ? d.KeyedImplementationType : d.ImplementationType;

    /// <summary>Registration errors that can be seen without resolving anything, thrown when the container is built.</summary>
    private static void Validate(ServiceDescriptor descriptor, string paramName)
    {
        Type service = descriptor.ServiceType;
        Type? implementation = ImplementationType(descriptor);
        if (service.IsGenericTypeDefinition)
        {
            if (implementation is null || !implementation.IsGenericTypeDefinition)
            {
                throw Errors.OpenGenericNeedsOpenImplementation(service, paramName);
            }

            if (implementation.IsAbstract || implementation.IsInterface)
            {
                throw Errors.CannotInstantiate(implementation, service);
            }

            if (service.GetGenericArguments().Length != implementation.GetGenericArguments().Length)
            {
                throw Errors.ArityMismatch(service, implementation, paramName);
            }
        }
        else if (implementation is not null && (implementation.IsGenericTypeDefinition || implementation.IsAbstract || implementation.IsInterface))
        {
            throw Errors.CannotInstantiate(implementation, service);
        }
    }
}
