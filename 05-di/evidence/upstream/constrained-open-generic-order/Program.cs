// GetService<IS<A>>() throws or succeeds depending on whether GetServices<IS<A>>() was called first on the same provider.
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddTransient(typeof(IS<>), typeof(Open<>));
services.AddTransient(typeof(IS<>), typeof(Constrained<>));   // A does not satisfy Constrained<T>'s constraint

using (var provider = services.BuildServiceProvider())
{
    Console.WriteLine($"GetService first:          {Describe(() => provider.GetService<IS<A>>())}");
}

using (var provider = services.BuildServiceProvider())
{
    Console.WriteLine($"GetServices first:         {provider.GetServices<IS<A>>().Count()} service(s)");
    Console.WriteLine($"then GetService:           {Describe(() => provider.GetService<IS<A>>())}");
}

static string Describe(Func<object?> resolve)
{
    try
    {
        return "OK, " + (resolve()?.GetType().Name ?? "null");
    }
    catch (Exception e)
    {
        return $"{e.GetType().Name}: {e.Message.Split('\n')[0]}";
    }
}

public interface IMarked;

public sealed class A;

public interface IS<T>;

public sealed class Open<T> : IS<T>;

public sealed class Constrained<T> : IS<T>
    where T : IMarked;
