using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI.Planning;

/// <summary>
/// <c>ValidateScopes</c>: refuses a singleton that would capture a scoped service (a "captive dependency"), and a
/// scoped service, or anything that needs one, resolved from the root provider, where it would silently live as long
/// as the container. Follows <c>ServiceLookup/CallSiteValidator.cs</c>: the first scoped service in each plan's tree is
/// worked out once and remembered, so the check on every resolution is one dictionary probe.
/// </summary>
/// <remarks>
/// The memo is keyed by <see cref="Plan.CacheKey"/>, as Microsoft's is keyed by <c>ResultCache.Key</c>, not by plan.
/// That matters: a constant (an instance registration, even a keyed one) has the key (unkeyed service type, 0), the
/// same as the unkeyed slot-0 registration of that type. So if that registration is scoped, a singleton depending on
/// a <em>keyed instance</em> of the type is reported as a captive dependency. It is a false positive in Microsoft's
/// container; the differential tests found it, and we reproduce it so the two containers fail the same builds.
/// </remarks>
internal sealed class ScopeValidator
{
    private readonly ConcurrentDictionary<InstanceKey, Type?> _firstScoped = new();

    /// <summary>Walks a newly built plan. Throws if a singleton in it depends, at any depth, on a scoped service.</summary>
    public void Validate(Plan plan) => Visit(plan, singleton: null);

    /// <summary>Called on every resolution when validation is on.</summary>
    public void ValidateResolution(Plan plan, Scope scope)
    {
        if (scope.IsRoot && _firstScoped.TryGetValue(plan.CacheKey, out Type? scoped) && scoped is not null)
        {
            throw plan.ServiceType == scoped ? Errors.DirectScopedFromRoot(scoped) : Errors.ScopedFromRoot(plan.ServiceType, scoped);
        }
    }

    private Type? Visit(Plan plan, Plan? singleton)
    {
        if (!_firstScoped.TryGetValue(plan.CacheKey, out Type? firstScoped))
        {
            firstScoped = VisitLocation(plan, singleton);
            _firstScoped[plan.CacheKey] = firstScoped;
        }

        if (firstScoped is not null && singleton is not null)
        {
            throw Errors.ScopedInSingleton(plan.ServiceType, singleton.ServiceType);
        }

        return firstScoped;
    }

    private Type? VisitLocation(Plan plan, Plan? singleton)
    {
        switch (plan.Location)
        {
            case CacheLocation.Root:
                return VisitChildren(plan, singleton: plan);
            case CacheLocation.Scope:
                // A singleton may hold the scope factory; that is how it is meant to create scopes.
                if (plan.ServiceType == typeof(IServiceScopeFactory))
                {
                    return null;
                }

                VisitChildren(plan, singleton);
                return plan.ServiceType;
            default:
                return VisitChildren(plan, singleton);
        }
    }

    private Type? VisitChildren(Plan plan, Plan? singleton)
    {
        Plan[] children = plan switch
        {
            ConstructorPlan constructor => constructor.Arguments,
            EnumerablePlan enumerable => enumerable.Items,
            _ => [],
        };

        Type? first = null;
        foreach (Plan child in children)
        {
            // Visit every child (not `first ??= Visit(...)`), so a captive dependency anywhere in the tree is found.
            Type? scoped = Visit(child, singleton);
            first ??= scoped;
        }

        return first;
    }
}
