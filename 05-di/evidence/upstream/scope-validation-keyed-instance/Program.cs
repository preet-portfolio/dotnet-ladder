// ValidateScopes reports a captive dependency that doesn't exist: a singleton that depends on a *keyed singleton
// instance* of IFoo is refused once the unrelated, unkeyed *scoped* IFoo registration has been validated.
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();
services.AddScoped<IFoo, Foo>();                        // unkeyed, scoped
services.AddKeyedSingleton<IFoo>("k", new Foo());       // keyed, a singleton instance
services.AddSingleton<Consumer>();                      // depends only on the keyed singleton

// 1. Without resolving the scoped IFoo first, Consumer resolves fine.
using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }))
{
    Console.WriteLine($"fresh provider:           {Describe(() => provider.GetRequiredService<Consumer>())}");
}

// 2. Resolve the scoped IFoo in a scope, then the same Consumer is refused.
using (var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }))
{
    using (var scope = provider.CreateScope())
    {
        scope.ServiceProvider.GetRequiredService<IFoo>();
    }

    Console.WriteLine($"after scoped IFoo:        {Describe(() => provider.GetRequiredService<Consumer>())}");
}

// 3. ValidateOnBuild walks every registration up front, in registration order, so it refuses the build outright.
Console.WriteLine($"ValidateOnBuild:          {Describe(() => services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }))}");

static string Describe(Func<object> resolve)
{
    try
    {
        return "OK, " + resolve().GetType().Name;
    }
    catch (Exception e)
    {
        return $"{e.GetType().Name}: {e.Message}";
    }
}

public interface IFoo;

public sealed class Foo : IFoo;

public sealed class Consumer([FromKeyedServices("k")] IFoo foo)
{
    public IFoo Foo { get; } = foo;
}
