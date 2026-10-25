using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI.Tests.Graph;

// The building blocks of random service graphs. Services are IS<K0>..IS<K5>; implementations are generic templates
// closed over a marker (which service they implement) and over their dependency types, so a generated graph is just
// data plus MakeGenericType. Every node takes the run's RunLog, which numbers it in creation order and records its
// disposal, so two containers can be compared on "same instances, created and disposed in the same order".

public interface IMarked;

public sealed class K0 : IMarked;

public sealed class K1;

public sealed class K2 : IMarked;

public sealed class K3;

public sealed class K4 : IMarked;

public sealed class K5;

#pragma warning disable CA1040 // a service with no members: identity is all that's compared
public interface IS<T>;
#pragma warning restore CA1040

/// <summary>Per-run recorder, registered as an instance, so every node can reach it without statics.</summary>
public sealed class RunLog
{
    private int _next;

    public List<string> Disposals { get; } = [];

    public int Created() => _next++;
}

public abstract class Node
{
    protected Node(RunLog log, params object?[] parts)
    {
        Log = log;
        Id = log.Created();
        Parts = parts;
    }

    public RunLog Log { get; }

    public int Id { get; }

    /// <summary>What was injected, in parameter order (dependencies, providers, keys, enumerations).</summary>
    public object?[] Parts { get; }
}

public sealed class Leaf<T>(RunLog log) : Node(log), IS<T>;

public sealed class Dep1<T, A>(RunLog log, A a) : Node(log, a), IS<T>;

public sealed class Dep2<T, A, B>(RunLog log, A a, B b) : Node(log, a, b), IS<T>;

public sealed class Many<T, A>(RunLog log, IEnumerable<A> all) : Node(log, all), IS<T>;

public sealed class WithProvider<T>(RunLog log, IServiceProvider provider) : Node(log, provider), IS<T>;

public sealed class Disposable<T, A>(RunLog log, A a) : Node(log, a), IS<T>, IDisposable
{
    public void Dispose() => Log.Disposals.Add($"D{Id}");
}

public sealed class AsyncDisposable<T, A>(RunLog log, A a) : Node(log, a), IS<T>, IAsyncDisposable
{
    public ValueTask DisposeAsync()
    {
        Log.Disposals.Add($"A{Id}");
        return ValueTask.CompletedTask;
    }
}

public sealed class BothDisposable<T, A>(RunLog log, A a) : Node(log, a), IS<T>, IDisposable, IAsyncDisposable
{
    public void Dispose() => Log.Disposals.Add($"D{Id}");

    public ValueTask DisposeAsync()
    {
        Log.Disposals.Add($"A{Id}");
        return ValueTask.CompletedTask;
    }
}

/// <summary>Constructor selection: the longest satisfiable constructor wins when the others are subsets of it.</summary>
public sealed class Longest<T, A, B> : Node, IS<T>
{
    public Longest(RunLog log, A a)
        : base(log, "A", a)
    {
    }

    public Longest(RunLog log, B b)
        : base(log, "B", b)
    {
    }

    public Longest(RunLog log, A a, B b)
        : base(log, "AB", a, b)
    {
    }
}

/// <summary>Constructor selection: ambiguous when both A and B are resolvable.</summary>
public sealed class Either<T, A, B> : Node, IS<T>
{
    public Either(RunLog log, A a)
        : base(log, "A", a)
    {
    }

    public Either(RunLog log, B b)
        : base(log, "B", b)
    {
    }
}

public sealed class KeyedDep<T, A>(RunLog log, [FromKeyedServices("k1")] A a) : Node(log, a), IS<T>;

public sealed class InheritedKeyDep<T, A>(RunLog log, [FromKeyedServices] A a) : Node(log, a), IS<T>;

public sealed class KeyAware<T>(RunLog log, [ServiceKey] object? key = null) : Node(log, key), IS<T>;

/// <summary>A [ServiceKey] of the wrong type for every key the graphs use (strings): refused whenever resolved with a key.</summary>
public sealed class IntKeyAware<T>(RunLog log, [ServiceKey] int key = 0) : Node(log, key), IS<T>;

public sealed class Optional<T, A>(RunLog log, A? a = null) : Node(log, a), IS<T>
    where A : class;

/// <summary>Creates its dependency, then fails: what was created before the failure must still be disposed.</summary>
public sealed class Throws<T, A> : Node, IS<T>
{
    public Throws(RunLog log, A a)
        : base(log, a) => throw new NotSupportedException($"node {Id} refuses to be built");
}

public sealed class Open<T>(RunLog log) : Node(log), IS<T>;

public sealed class OpenDisposable<T>(RunLog log) : Node(log), IS<T>, IDisposable
{
    public void Dispose() => Log.Disposals.Add($"D{Id}");
}

public sealed class OpenConstrained<T>(RunLog log) : Node(log), IS<T>
    where T : IMarked;
