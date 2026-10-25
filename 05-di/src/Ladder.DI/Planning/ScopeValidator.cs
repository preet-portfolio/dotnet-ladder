using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI.Planning;

/// <summary>
/// <c>ValidateScopes</c>: refuses a singleton that would capture a scoped service (a "captive dependency"), and a
/// scoped service, or anything that needs one, resolved from the root provider, where it would silently live as long
/// as the container. Follows <c>ServiceLookup/CallSiteValidator.cs</c>: each plan remembers the first scoped service
/// in its tree, so the check on every resolution is one field read.
/// </summary>
internal static class ScopeValidator
{
    /// <summary>Walks a newly built plan. Throws if a singleton in it depends, at any depth, on a scoped service.</summary>
    public static void Validate(Plan plan) => Visit(plan, singleton: null);

    /// <summary>Called on every resolution when validation is on.</summary>
    public static void ValidateResolution(Plan plan, Scope scope)
    {
        if (scope.IsRoot && plan.ScopeChecked && plan.FirstScopedService is { } scoped)
        {
            throw plan.ServiceType == scoped ? Errors.DirectScopedFromRoot(scoped) : Errors.ScopedFromRoot(plan.ServiceType, scoped);
        }
    }

    private static Type? Visit(Plan plan, Plan? singleton)
    {
        Type? firstScoped;
        if (plan.ScopeChecked)
        {
            firstScoped = plan.FirstScopedService;
        }
        else
        {
            firstScoped = VisitLocation(plan, singleton);
            plan.FirstScopedService = firstScoped;
            plan.ScopeChecked = true;
        }

        if (firstScoped is not null && singleton is not null)
        {
            throw Errors.ScopedInSingleton(plan.ServiceType, singleton.ServiceType);
        }

        return firstScoped;
    }

    private static Type? VisitLocation(Plan plan, Plan? singleton)
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

    private static Type? VisitChildren(Plan plan, Plan? singleton)
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
