using System.Linq.Expressions;
using System.Reflection;
using Ladder.DI.Planning;

namespace Ladder.DI.Resolution;

/// <summary>
/// Compiles a plan into a <c>Func&lt;Scope, object?&gt;</c> with expression trees, so a warm resolution is straight-line
/// code: <c>new</c> calls with their arguments in place, no reflection, no argument arrays.
/// <list type="bullet">
/// <item>Singletons are embedded as constants (Microsoft's <c>ExpressionResolverBuilder</c> does the same). Compiling
/// on the second call means the first call has already created them.</item>
/// <item>Scoped services become a call to <see cref="Scope.GetOrCreateScoped"/>, whose slow path runs the scoped plan's
/// own compiled body.</item>
/// <item>A transient is wrapped in <see cref="Scope.Capture"/> only if its type can be disposable, which a constructor
/// plan knows statically, so non-disposable transients cost exactly their allocation.</item>
/// </list>
/// </summary>
internal static class PlanCompiler
{
    private static readonly MethodInfo GetOrCreateScoped = typeof(Scope).GetMethod(nameof(Scope.GetOrCreateScoped))!;
    private static readonly MethodInfo Capture = typeof(Scope).GetMethod(nameof(Scope.Capture))!;
    private static readonly MethodInfo ResolveRoot = typeof(Interpreter).GetMethod(nameof(Interpreter.ResolveRoot))!;

    /// <summary>Compiles a whole resolution: caching included, as <see cref="Interpreter.Resolve"/> would do it.</summary>
    public static Func<Scope, object?> Compile(Plan plan, Scope root)
    {
        ParameterExpression scope = Expression.Parameter(typeof(Scope), "scope");
        return Lambda(Resolve(plan, scope, root), scope);
    }

    private static Func<Scope, object?> Lambda(Expression body, ParameterExpression scope) =>
        Expression.Lambda<Func<Scope, object?>>(ConvertTo(body, typeof(object)), scope).Compile();

    private static Expression Resolve(Plan plan, ParameterExpression scope, Scope root)
    {
        switch (plan.Location)
        {
            case CacheLocation.Root:
                // An existing singleton is embedded. One that doesn't exist yet (only possible when compiling before
                // the first resolution) is created lazily at its turn, so creation and disposal order don't change.
                return Volatile.Read(ref plan.RootValue) is { } value
                    ? Constant(value)
                    : Expression.Call(ResolveRoot, Expression.Constant(plan, typeof(Plan)), Expression.Constant(root));
            case CacheLocation.Scope:
                if (plan.CompiledBody is null)
                {
                    // Compile the body once per plan; racing compilations produce equivalent delegates.
                    ParameterExpression bodyScope = Expression.Parameter(typeof(Scope), "scope");
                    plan.CompiledBody = Lambda(Create(plan, bodyScope, root), bodyScope);
                }

                return Expression.Call(scope, GetOrCreateScoped, Expression.Constant(plan));
            case CacheLocation.Dispose when plan.MayNeedDisposal:
                Expression created = Create(plan, scope, root);
                return ConvertTo(Expression.Call(scope, Capture, ConvertTo(created, typeof(object))), created.Type);
            default:
                return Create(plan, scope, root);
        }
    }

    private static Expression Create(Plan plan, ParameterExpression scope, Scope root)
    {
        switch (plan)
        {
            case ConstantPlan constant:
                return Constant(constant.Value);
            case ScopePlan:
                return scope;
            case ConstructorPlan constructor:
                ParameterInfo[] parameters = constructor.Constructor.GetParameters();
                var arguments = new Expression[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    arguments[i] = ConvertTo(Resolve(constructor.Arguments[i], scope, root), parameters[i].ParameterType);
                }

                return Expression.New(constructor.Constructor, arguments);
            case FactoryPlan { Factory: { } factory }:
                return Expression.Invoke(Expression.Constant(factory), scope);
            case FactoryPlan { KeyedFactory: { } keyedFactory } keyed:
                return Expression.Invoke(Expression.Constant(keyedFactory), scope, Expression.Constant(keyed.Key, typeof(object)));
            case EnumerablePlan enumerable:
                return Expression.NewArrayInit(enumerable.ItemType, enumerable.Items.Select(item => ConvertTo(Resolve(item, scope, root), enumerable.ItemType)));
            default:
                throw new InvalidOperationException($"Unknown plan {plan.GetType()}.");
        }
    }

    /// <summary>A constant typed as its runtime type, so using it as an argument needs no cast.</summary>
    private static ConstantExpression Constant(object? value) => Expression.Constant(value, value?.GetType() ?? typeof(object));

    private static Expression ConvertTo(Expression expression, Type type)
    {
        bool reference = !expression.Type.IsValueType && !type.IsValueType;
        return expression.Type == type || (reference && type.IsAssignableFrom(expression.Type)) ? expression : Expression.Convert(expression, type);
    }
}
