using System.Reflection;
using Ladder.DI.Planning;

namespace Ladder.DI.Resolution;

/// <summary>
/// Executes a plan by walking it, with reflection for constructors. It serves the first resolution of every service
/// (compiling something that may be asked for once is a loss), and all of them when compilation is turned off.
/// </summary>
internal static class Interpreter
{
    /// <summary>Resolves a plan in a scope, honouring where its result is cached.</summary>
    public static object? Resolve(Plan plan, Scope scope) => plan.Location switch
    {
        CacheLocation.Root => ResolveRoot(plan, scope.Root),
        CacheLocation.Scope => scope.GetOrCreateScoped(plan),
        CacheLocation.Dispose => plan.MayNeedDisposal ? scope.Capture(Create(plan, scope)) : Create(plan, scope),
        _ => Create(plan, scope),
    };

    /// <summary>
    /// The root instance of a singleton (or of a scoped service resolved from the root), created once under a lock
    /// on its plan. It is created <em>in the root scope</em>: its transient dependencies are captured by the root and
    /// an injected <see cref="IServiceProvider"/> is the root, whichever scope asked first (<c>VisitRootCache</c>).
    /// </summary>
    public static object? ResolveRoot(Plan plan, Scope root)
    {
        if (Volatile.Read(ref plan.RootValue) is { } value)
        {
            return value;
        }

        lock (plan)
        {
            if (plan.RootValue is { } raced)
            {
                return raced;
            }

            object? created = plan.CompiledBody is { } body ? body(root) : Create(plan, root);
            root.Capture(created);
            Volatile.Write(ref plan.RootValue, created);
            return created;
        }
    }

    /// <summary>Produces a new result of the plan, without consulting or filling any cache.</summary>
    public static object? Create(Plan plan, Scope scope)
    {
        switch (plan)
        {
            case ConstantPlan constant:
                return constant.Value;
            case ScopePlan:
                return scope;
            case ConstructorPlan constructor:
                Plan[] argumentPlans = constructor.Arguments;
                object?[] arguments = argumentPlans.Length == 0 ? [] : new object?[argumentPlans.Length];
                for (int i = 0; i < argumentPlans.Length; i++)
                {
                    arguments[i] = Resolve(argumentPlans[i], scope);
                }

                // DoNotWrapExceptions: a constructor's exception reaches the caller as itself, not as TargetInvocationException.
                return constructor.Constructor.Invoke(BindingFlags.DoNotWrapExceptions, binder: null, arguments, culture: null);
            case FactoryPlan factory:
                return factory.Factory is { } unkeyed ? unkeyed(scope) : factory.KeyedFactory!(scope, factory.Key);
            case EnumerablePlan enumerable:
                var array = Array.CreateInstance(enumerable.ItemType, enumerable.Items.Length);
                for (int i = 0; i < enumerable.Items.Length; i++)
                {
                    array.SetValue(Resolve(enumerable.Items[i], scope), i);
                }

                return array;
            default:
                throw new InvalidOperationException($"Unknown plan {plan.GetType()}.");
        }
    }
}
