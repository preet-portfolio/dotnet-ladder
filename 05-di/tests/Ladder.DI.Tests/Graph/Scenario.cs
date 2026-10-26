using System.Collections;
using System.Text;
using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI.Tests.Graph;

public enum Template
{
    Leaf, Dep1, Dep2, Many, WithProvider, Disposable, AsyncDisposable, BothDisposable, Longest, Either,
    KeyedDep, InheritedKeyDep, KeyAware, IntKeyAware, Optional, Throws, Factory, Instance, Open, OpenDisposable, OpenConstrained,
}

public enum OpKind { Get, GetKeyed, GetAll, GetAllKeyed, GetRequired, CreateScope, Dispose, DisposeAsync }

/// <summary>
/// One registration. <see cref="Key"/> indexes <see cref="Scenario.Keys"/>; <see cref="DepA"/>/<see cref="DepB"/>
/// encode a dependency: below <see cref="Scenario.Services"/> a service, below twice that an <c>IEnumerable</c> of
/// one, otherwise the RunLog.
/// </summary>
public sealed record Registration(int Service, ServiceLifetime Lifetime, Template Template, int Key, int DepA, int DepB);

public sealed record Op(OpKind Kind, int Service, int Key, int Scope);

/// <summary>
/// A random service graph and a script to run against it. In acyclic scenarios every dependency points to a
/// lower-numbered service and factories resolve dependencies through the provider; in cyclic ones dependencies may
/// point anywhere (so cycles are planned and must be refused) and factories resolve nothing, because a cycle through
/// a factory is invisible to planning and overflows the stack in every container.
/// </summary>
public sealed record Scenario(bool Acyclic, bool ValidateScopes, bool ValidateOnBuild, ResolutionMode Mode, Registration[] Registrations, Op[] Ops, bool DisposeRootAsync)
{
    public const int Services = 6;

    public static readonly Type[] Markers = [typeof(K0), typeof(K1), typeof(K2), typeof(K3), typeof(K4), typeof(K5)];

    /// <summary>Registration keys use the first four; operations also ask for "k3", which nothing registers.</summary>
    public static readonly object?[] Keys = [null, "k1", "k2", KeyedService.AnyKey, "k3"];

    public override string ToString() =>
        $"Acyclic={Acyclic} ValidateScopes={ValidateScopes} ValidateOnBuild={ValidateOnBuild} Mode={Mode} DisposeRootAsync={DisposeRootAsync}\n"
        + string.Join("\n", Registrations.Select(r => "  " + r)) + "\n"
        + string.Join("\n", Ops.Select(o => "  " + o));
}

/// <summary>Runs a scenario against a container and writes down everything observable, one line per step.</summary>
public static class ScenarioRunner
{
    public static string Run(Scenario scenario, Func<IServiceCollection, Scenario, IServiceProvider> build)
    {
        var log = new RunLog();
        var lines = new List<string>();
        var describer = new Describer();
        IServiceProvider root;
        try
        {
            root = build(Materialize(scenario, log), scenario);
        }
#pragma warning disable CA1031 // every exception is an observation to compare
        catch (Exception e)
        {
            return "build " + Describer.Exception(e);
        }

        // Index 0 is the container itself; scopes are appended as the script creates them.
        var scopes = new List<(IServiceProvider Provider, object Handle)> { (root, root) };
        foreach (Op op in scenario.Ops)
        {
            try
            {
                lines.Add(Execute(op, scopes, describer));
            }
            catch (Exception e)
            {
                lines.Add($"{op.Kind} {Describer.Exception(e)}");
            }
        }

        try
        {
            Dispose(root, scenario.DisposeRootAsync);
            lines.Add("root disposed");
        }
        catch (Exception e)
        {
            lines.Add("root dispose " + Describer.Exception(e));
        }
#pragma warning restore CA1031

        lines.Add("disposals " + string.Join(",", log.Disposals));
        return string.Join("\n", lines);
    }

    private static string Execute(Op op, List<(IServiceProvider Provider, object Handle)> scopes, Describer describer)
    {
        int index = op.Scope % scopes.Count;
        IServiceProvider provider = scopes[index].Provider;
        Type service = typeof(IS<>).MakeGenericType(Scenario.Markers[op.Service % Scenario.Services]);
        object? key = Scenario.Keys[op.Key % Scenario.Keys.Length];
        switch (op.Kind)
        {
            case OpKind.Get:
                return $"get {Describer.Name(service)} in {index}: {describer.Describe(provider.GetService(service))}";
            case OpKind.GetKeyed:
                return $"get {Describer.Name(service)} [{Describer.Key(key)}] in {index}: {describer.Describe(provider.GetKeyedService(service, key))}";
            case OpKind.GetAll:
                return $"all {Describer.Name(service)} in {index}: {describer.Describe(provider.GetServices(service))}";
            case OpKind.GetAllKeyed:
                return $"all {Describer.Name(service)} [{Describer.Key(key)}] in {index}: {describer.Describe(provider.GetKeyedServices(service, key))}";
            case OpKind.GetRequired:
                return $"required {Describer.Name(service)} in {index}: {describer.Describe(provider.GetRequiredService(service))}";
            case OpKind.CreateScope:
                IServiceScope scope = provider.GetRequiredService<IServiceScopeFactory>().CreateScope();
                scopes.Add((scope.ServiceProvider, scope));
                describer.Label(scope.ServiceProvider, $"scope{scopes.Count - 1}");
                return $"scope {scopes.Count - 1} from {index}";
            default:
                // Only scopes: the container is disposed once, at the end, so most scripts keep resolving after a
                // scope is gone (use-after-dispose is still exercised through the disposed scopes).
                if (scopes.Count == 1)
                {
                    return "no scope to dispose";
                }

                int target = 1 + (op.Scope % (scopes.Count - 1));
                Dispose(scopes[target].Handle, op.Kind == OpKind.DisposeAsync);
                return $"disposed {target}";
        }
    }

    private static void Dispose(object handle, bool async)
    {
        if (async && handle is IAsyncDisposable asyncDisposable)
        {
            asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        else
        {
            ((IDisposable)handle).Dispose();
        }
    }

    private static IServiceCollection Materialize(Scenario scenario, RunLog log)
    {
        IServiceCollection services = new ServiceCollection();
        services.AddSingleton(log);
        foreach (Registration r in scenario.Registrations)
        {
            services.Add(Descriptor(r, scenario.Acyclic, log));
        }

        return services;
    }

    private static ServiceDescriptor Descriptor(Registration r, bool acyclic, RunLog log)
    {
        int self = r.Service % Scenario.Services;
        Type marker = Scenario.Markers[self];
        Type service = typeof(IS<>).MakeGenericType(marker);
        object? key = Scenario.Keys[r.Key % 4];
        Type a = Dependency(r.DepA, self, acyclic);
        Type b = Dependency(r.DepB, self, acyclic);

        switch (r.Template)
        {
            case Template.Instance:
                object instance = Activator.CreateInstance(typeof(Leaf<>).MakeGenericType(marker), log)!;
                return key is null ? new ServiceDescriptor(service, instance) : new ServiceDescriptor(service, key, instance);
            case Template.Factory when key is null:
                Type unkeyedProduct = typeof(Dep1<,>).MakeGenericType(marker, typeof(object));
                return new ServiceDescriptor(service, sp => Activator.CreateInstance(unkeyedProduct, log, acyclic ? sp.GetService(a) : null)!, r.Lifetime);
            case Template.Factory:
                Type keyedProduct = typeof(Dep2<,,>).MakeGenericType(marker, typeof(object), typeof(object));
                return new ServiceDescriptor(service, key, (sp, k) => Activator.CreateInstance(keyedProduct, log, k, acyclic ? sp.GetService(a) : null)!, r.Lifetime);
            case Template.Open or Template.OpenDisposable or Template.OpenConstrained:
                Type open = r.Template switch
                {
                    Template.Open => typeof(Open<>),
                    Template.OpenDisposable => typeof(OpenDisposable<>),
                    _ => typeof(OpenConstrained<>),
                };
                return new ServiceDescriptor(typeof(IS<>), key, open, r.Lifetime);
            default:
                Type implementation = r.Template switch
                {
                    Template.Leaf => typeof(Leaf<>).MakeGenericType(marker),
                    Template.Dep1 => typeof(Dep1<,>).MakeGenericType(marker, a),
                    Template.Dep2 => typeof(Dep2<,,>).MakeGenericType(marker, a, b),
                    Template.Many => typeof(Many<,>).MakeGenericType(marker, ItemOf(a)),
                    Template.WithProvider => typeof(WithProvider<>).MakeGenericType(marker),
                    Template.Disposable => typeof(Disposable<,>).MakeGenericType(marker, a),
                    Template.AsyncDisposable => typeof(AsyncDisposable<,>).MakeGenericType(marker, a),
                    Template.BothDisposable => typeof(BothDisposable<,>).MakeGenericType(marker, a),
                    Template.Longest => typeof(Longest<,,>).MakeGenericType(marker, a, b),
                    Template.Either => typeof(Either<,,>).MakeGenericType(marker, a, b),
                    Template.KeyedDep => typeof(KeyedDep<,>).MakeGenericType(marker, a),
                    Template.InheritedKeyDep => typeof(InheritedKeyDep<,>).MakeGenericType(marker, a),
                    Template.KeyAware => typeof(KeyAware<>).MakeGenericType(marker),
                    Template.IntKeyAware => typeof(IntKeyAware<>).MakeGenericType(marker),
                    Template.Optional => typeof(Optional<,>).MakeGenericType(marker, a),
                    _ => typeof(Throws<,>).MakeGenericType(marker, a),
                };
                return key is null ? new ServiceDescriptor(service, implementation, r.Lifetime) : new ServiceDescriptor(service, key, implementation, r.Lifetime);
        }
    }

    private static Type Dependency(int raw, int self, bool acyclic)
    {
        raw %= 2 * Scenario.Services + 1;
        if (raw == 2 * Scenario.Services || (acyclic && self == 0))
        {
            return typeof(RunLog);
        }

        int target = raw % Scenario.Services;
        if (acyclic)
        {
            target %= self; // strictly lower-numbered: no path can come back
        }

        Type dependency = typeof(IS<>).MakeGenericType(Scenario.Markers[target]);
        return raw >= Scenario.Services ? typeof(IEnumerable<>).MakeGenericType(dependency) : dependency;
    }

    private static Type ItemOf(Type dependency) =>
        dependency.IsGenericType && dependency.GetGenericTypeDefinition() == typeof(IEnumerable<>) ? dependency.GenericTypeArguments[0] : dependency;
}

/// <summary>
/// Writes an object graph in a container-independent form. A node prints in full the first time it is seen and as
/// <c>#id</c> afterwards, ids being creation order, so equal strings mean the same instances were shared, in the same
/// places, created in the same order. Providers are named by first appearance (or by the scope that owns them).
/// </summary>
public sealed partial class Describer
{
    private readonly HashSet<Node> _seen = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<object, string> _providers = new(ReferenceEqualityComparer.Instance);

    public void Label(IServiceProvider provider, string label) => _providers.TryAdd(provider, label);

    public string Describe(object? value)
    {
        switch (value)
        {
            case null:
                return "null";
            case Node node when !_seen.Add(node):
                return $"#{node.Id}";
            case Node node:
                return $"{Name(node.GetType())}#{node.Id}({string.Join(",", node.Parts.Select(Describe))})";
            case IServiceProvider provider:
                if (!_providers.TryGetValue(provider, out string? label))
                {
                    _providers[provider] = label = $"sp{_providers.Count}";
                }

                return label;
            case string text:
                return $"'{text}'";
            case IEnumerable items:
                return "[" + string.Join(",", items.Cast<object?>().Select(Describe)) + "]";
            default:
                return Key(value);
        }
    }

    /// <summary>The observations without exception messages, which is what the containers must agree on.</summary>
    public static string Comparable(string observations) => MessagePattern().Replace(observations, "");

    [System.Text.RegularExpressions.GeneratedRegex(@" \{[^{}]*\}")]
    private static partial System.Text.RegularExpressions.Regex MessagePattern();

    public static string Key(object? key) => key is null ? "null" : ReferenceEquals(key, KeyedService.AnyKey) ? "AnyKey" : key.ToString()!;

    /// <summary>
    /// The exception's type, plus for InvalidOperationException which rule it reports (taken from the message, whose
    /// wording both containers share), so "refused a cycle" and "refused an ambiguous constructor" don't compare equal.
    /// ValidateOnBuild's per-descriptor wrapper is shown with what it wraps.
    /// </summary>
    public static string Exception(Exception e)
    {
        var text = new StringBuilder("!").Append(e.GetType().Name);
        if (e is AggregateException aggregate)
        {
            return text.Append('(').AppendJoin(",", aggregate.InnerExceptions.Select(Exception)).Append(')').ToString();
        }

        bool plain = e.GetType() == typeof(InvalidOperationException);
        if (plain && e.InnerException is { } wrapped && e.Message.StartsWith("Error while validating", StringComparison.Ordinal))
        {
            return text.Append('<').Append(Exception(wrapped)).Append('>').ToString();
        }

        if (plain)
        {
            text.Append(':').Append(Kind(e.Message));
        }

        // Carried for diagnosing a divergence, but stripped before comparing (see Comparable): wording isn't claimed.
        return text.Append(" {").Append(e.Message.Replace('{', '(').Replace('}', ')')).Append('}').ToString();
    }

    private static string Kind(string message) => message switch
    {
        _ when message.StartsWith("A circular dependency", StringComparison.Ordinal) => "cycle",
        _ when message.Contains("constructors are ambiguous", StringComparison.Ordinal) => "ambiguous",
        _ when message.StartsWith("Cannot consume", StringComparison.Ordinal) => "captive",
        _ when message.Contains("from root provider", StringComparison.Ordinal) => "scoped-from-root",
        _ when message.StartsWith("Unable to resolve service", StringComparison.Ordinal) => "unresolvable",
        _ when message.StartsWith("No constructor for type", StringComparison.Ordinal) => "no-satisfiable-constructor",
        _ when message.StartsWith("KeyedService.AnyKey", StringComparison.Ordinal) => "anykey",
        _ when message.StartsWith("No service for type", StringComparison.Ordinal) || message.StartsWith("No keyed service", StringComparison.Ordinal) => "missing",
        _ when message.Contains("only implements IAsyncDisposable", StringComparison.Ordinal) => "async-only",
        _ when message.StartsWith("The type of the key", StringComparison.Ordinal) => "key-type",
        _ => "other: " + message,
    };

    public static string Name(Type type)
    {
        if (!type.IsGenericType)
        {
            return type.Name;
        }

        string name = type.Name[..type.Name.IndexOf('`', StringComparison.Ordinal)];
        return $"{name}<{string.Join(",", type.GenericTypeArguments.Select(Name))}>";
    }
}
