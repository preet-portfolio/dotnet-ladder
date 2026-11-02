using Microsoft.Extensions.DependencyInjection;

// Microsoft.Extensions.DependencyInjection: resolving a keyed service whose only match is an AnyKey registration,
// when that service depends on IEnumerable<> of its own type under a specific key that an open generic registration
// serves, throws ArgumentException from an internal dictionary. Each case below changes one ingredient.
Run("1. scoped AnyKey closed + scoped \"k1\" open generic", s =>
{
    s.AddKeyedScoped(typeof(IS<K>), KeyedService.AnyKey, typeof(KeyedDep<K>));
    s.AddKeyedScoped(typeof(IS<>), "k1", typeof(Open<>));
});
Run("2. same, transient", s =>
{
    s.AddKeyedTransient(typeof(IS<K>), KeyedService.AnyKey, typeof(KeyedDep<K>));
    s.AddKeyedTransient(typeof(IS<>), "k1", typeof(Open<>));
});
Run("3. same, singleton", s =>
{
    s.AddKeyedSingleton(typeof(IS<K>), KeyedService.AnyKey, typeof(KeyedDep<K>));
    s.AddKeyedSingleton(typeof(IS<>), "k1", typeof(Open<>));
});
Run("4. \"k1\" registration closed instead of open generic", s =>
{
    s.AddKeyedScoped(typeof(IS<K>), KeyedService.AnyKey, typeof(KeyedDep<K>));
    s.AddKeyedScoped(typeof(IS<K>), "k1", typeof(Open<K>));
});
Run("5. dependency is IS<K> \"k1\" (not IEnumerable)", s =>
{
    s.AddKeyedScoped(typeof(IS<K>), KeyedService.AnyKey, typeof(SingleDep<K>));
    s.AddKeyedScoped(typeof(IS<>), "k1", typeof(Open<>));
});
Run("6. resolve IEnumerable<IS<K>> \"k1\" directly", s =>
{
    s.AddKeyedScoped(typeof(IS<K>), KeyedService.AnyKey, typeof(KeyedDep<K>));
    s.AddKeyedScoped(typeof(IS<>), "k1", typeof(Open<>));
}, sp => string.Join(", ", sp.GetKeyedServices<IS<K>>("k1")));
Run("7. resolve with key \"k2\" (served only by AnyKey)", s =>
{
    s.AddKeyedScoped(typeof(IS<K>), KeyedService.AnyKey, typeof(KeyedDep<K>));
    s.AddKeyedScoped(typeof(IS<>), "k1", typeof(Open<>));
}, sp => sp.GetKeyedService<IS<K>>("k2")?.ToString());
Run("8. singleton: resolve the service, then the \"k1\" enumerable", s =>
{
    s.AddKeyedSingleton(typeof(IS<K>), KeyedService.AnyKey, typeof(KeyedDep<K>));
    s.AddKeyedSingleton(typeof(IS<>), "k1", typeof(Open<>));
}, sp => $"{sp.GetKeyedService<IS<K>>("k1")} then [{string.Join(", ", sp.GetKeyedServices<IS<K>>("k1"))}]");
Run("9. scoped: resolve the \"k1\" enumerable, then the service", s =>
{
    s.AddKeyedScoped(typeof(IS<K>), KeyedService.AnyKey, typeof(KeyedDep<K>));
    s.AddKeyedScoped(typeof(IS<>), "k1", typeof(Open<>));
}, sp => $"[{string.Join(", ", sp.GetKeyedServices<IS<K>>("k1"))}] then {sp.GetKeyedService<IS<K>>("k1")}");
Run("10. singleton: resolve the \"k1\" enumerable, then the service", s =>
{
    s.AddKeyedSingleton(typeof(IS<K>), KeyedService.AnyKey, typeof(KeyedDep<K>));
    s.AddKeyedSingleton(typeof(IS<>), "k1", typeof(Open<>));
}, sp => $"[{string.Join(", ", sp.GetKeyedServices<IS<K>>("k1"))}] then {sp.GetKeyedService<IS<K>>("k1")}");

static void Run(string name, Action<IServiceCollection> register, Func<IServiceProvider, string?>? resolve = null)
{
    var services = new ServiceCollection();
    register(services);
    using var root = services.BuildServiceProvider();
    using var scope = root.CreateScope();
    resolve ??= sp => sp.GetKeyedService<IS<K>>("k1")?.ToString();
    try
    {
        Console.WriteLine($"{name}: {resolve(scope.ServiceProvider) ?? "null"}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"{name}: {e.GetType().Name}: {e.Message}");
    }
}

public interface IS<T>;
public sealed class K;
public sealed class Open<T> : IS<T>
{
    public override string ToString() => $"Open<{typeof(T).Name}>";
}
public sealed class KeyedDep<T>([FromKeyedServices("k1")] IEnumerable<IS<T>> all) : IS<T>
{
    public override string ToString() => $"KeyedDep([{string.Join(", ", all)}])";
}
public sealed class SingleDep<T>([FromKeyedServices("k1")] IS<T> one) : IS<T>
{
    public override string ToString() => $"SingleDep({one})";
}
