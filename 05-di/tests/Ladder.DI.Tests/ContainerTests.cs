using Ladder.DI.Tests.Graph;
using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI.Tests;

public class ContainerTests
{
    /// <summary>Both containers, so behaviour that copies a Microsoft quirk is pinned against the original.</summary>
    public static TheoryData<string> Containers => ["ours", "microsoft"];

    private static IServiceProvider Build(string container, IServiceCollection services, bool validateScopes = false) => container == "ours"
        ? services.BuildLadderServiceProvider(new ServiceContainerOptions { ValidateScopes = validateScopes })
        : services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes });

    [Fact]
    public void Disposes_what_a_scope_created_in_reverse_order_of_creation()
    {
        var log = new RunLog();
        var services = new ServiceCollection()
            .AddSingleton(log)
            .AddScoped<IS<K0>, Disposable<K0, RunLog>>()
            .AddTransient<IS<K1>, Disposable<K1, IS<K0>>>()
            .AddScoped<IS<K2>, Disposable<K2, IS<K1>>>();
        using ServiceContainer container = services.BuildLadderServiceProvider();

        using (IServiceScope scope = container.CreateScope())
        {
            var top = (Node)scope.ServiceProvider.GetRequiredService<IS<K2>>(); // creates K0 (#0), K1 (#1), K2 (#2)
            Assert.Equal(2, top.Id);
            scope.ServiceProvider.GetRequiredService<IS<K1>>(); // another transient K1 (#3), same scoped K0
        }

        Assert.Equal(["D3", "D2", "D1", "D0"], log.Disposals);
    }

    [Fact]
    public async Task An_IAsyncDisposable_only_service_needs_DisposeAsync()
    {
        var log = new RunLog();
        var services = new ServiceCollection().AddSingleton(log).AddScoped<IS<K0>, AsyncDisposable<K0, RunLog>>();
        using ServiceContainer container = services.BuildLadderServiceProvider();

        IServiceScope syncScope = container.CreateScope();
        syncScope.ServiceProvider.GetRequiredService<IS<K0>>();
        InvalidOperationException e = Assert.Throws<InvalidOperationException>(syncScope.Dispose);
        Assert.Contains("only implements IAsyncDisposable", e.Message, StringComparison.Ordinal);

        AsyncServiceScope asyncScope = container.CreateAsyncScope();
        asyncScope.ServiceProvider.GetRequiredService<IS<K0>>();
        await asyncScope.DisposeAsync();
        Assert.Equal(["A1"], log.Disposals);
    }

    [Fact]
    public void Instances_supplied_by_the_caller_are_not_disposed_but_factory_results_are()
    {
        var log = new RunLog();
        var supplied = new Disposable<K0, RunLog>(log, log);
        var services = new ServiceCollection()
            .AddSingleton(log)
            .AddSingleton<IS<K0>>(supplied)
            .AddSingleton<IS<K1>>(_ => new Disposable<K1, RunLog>(log, log));
        ServiceContainer container = services.BuildLadderServiceProvider();
        container.GetRequiredService<IS<K0>>();
        container.GetRequiredService<IS<K1>>();

        container.Dispose();

        Assert.Equal(["D1"], log.Disposals);
    }

    [Fact]
    public void A_singleton_first_resolved_in_a_scope_still_lives_in_the_root()
    {
        var log = new RunLog();
        var services = new ServiceCollection()
            .AddSingleton(log)
            .AddTransient<IS<K0>, Disposable<K0, RunLog>>()
            .AddSingleton<IS<K1>, Dep2<K1, IS<K0>, IServiceProvider>>();
        using ServiceContainer container = services.BuildLadderServiceProvider();

        Node singleton;
        using (IServiceScope scope = container.CreateScope())
        {
            singleton = (Node)scope.ServiceProvider.GetRequiredService<IS<K1>>();
            Assert.NotSame(scope.ServiceProvider, singleton.Parts[1]);
        }

        // Its transient dependency was captured by the root, so the scope didn't dispose it.
        Assert.Empty(log.Disposals);
        Assert.Same(container.GetRequiredService<IServiceProvider>(), singleton.Parts[1]);
    }

    [Theory]
    [MemberData(nameof(Containers))]
    public void ValidateScopes_refuses_captive_dependencies_and_scoped_services_from_the_root(string container)
    {
        IServiceCollection captive = new ServiceCollection().AddSingleton(new RunLog())
            .AddScoped<IS<K0>, Leaf<K0>>()
            .AddSingleton<IS<K1>, Dep1<K1, IS<K0>>>();
        IServiceProvider provider = Build(container, captive, validateScopes: true);

        Assert.Contains("Cannot consume scoped service", Assert.Throws<InvalidOperationException>(() => provider.GetService<IS<K1>>()).Message, StringComparison.Ordinal);
        Assert.Contains("from root provider", Assert.Throws<InvalidOperationException>(() => provider.GetService<IS<K0>>()).Message, StringComparison.Ordinal);
        using IServiceScope scope = provider.CreateScope();
        Assert.NotNull(scope.ServiceProvider.GetService<IS<K0>>());

        // Without validation the captive dependency is allowed, and the scoped service lives as long as the singleton.
        Assert.NotNull(Build(container, captive).GetService<IS<K1>>());
    }

    [Fact]
    public void ValidateOnBuild_reports_every_broken_registration_at_once()
    {
        IServiceCollection services = new ServiceCollection().AddSingleton(new RunLog())
            .AddSingleton<IS<K0>, Dep1<K0, IS<K5>>>()                // missing dependency
            .AddSingleton<IS<K1>, Dep1<K1, IS<K2>>>()                // cycle K1 -> K2 -> K1
            .AddSingleton<IS<K2>, Dep1<K2, IS<K1>>>()
            .AddSingleton<IS<K3>, Leaf<K3>>();

        AggregateException e = Assert.Throws<AggregateException>(() => services.BuildLadderServiceProvider(new ServiceContainerOptions { ValidateOnBuild = true }));

        Assert.Equal(3, e.InnerExceptions.Count);
        Assert.All(e.InnerExceptions, inner => Assert.StartsWith("Error while validating the service descriptor", inner.Message, StringComparison.Ordinal));
    }

    [Fact]
    public void A_cycle_is_refused_before_anything_is_constructed_and_the_path_is_named()
    {
        var log = new RunLog();
        IServiceCollection services = new ServiceCollection().AddSingleton(log)
            .AddTransient<IS<K0>, Dep1<K0, IS<K1>>>()
            .AddTransient<IS<K1>, Dep1<K1, IS<K0>>>();
        using ServiceContainer container = services.BuildLadderServiceProvider();

        InvalidOperationException e = Assert.Throws<InvalidOperationException>(() => container.GetService<IS<K0>>());

        Assert.Equal(0, log.Created()); // nothing was built
        Assert.Contains(
            "Ladder.DI.Tests.Graph.IS<Ladder.DI.Tests.Graph.K0>(Ladder.DI.Tests.Graph.Dep1<Ladder.DI.Tests.Graph.K0, Ladder.DI.Tests.Graph.IS<Ladder.DI.Tests.Graph.K1>>) -> "
            + "Ladder.DI.Tests.Graph.IS<Ladder.DI.Tests.Graph.K1>(Ladder.DI.Tests.Graph.Dep1<Ladder.DI.Tests.Graph.K1, Ladder.DI.Tests.Graph.IS<Ladder.DI.Tests.Graph.K0>>) -> "
            + "Ladder.DI.Tests.Graph.IS<Ladder.DI.Tests.Graph.K0>",
            e.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Everything_refuses_to_resolve_after_disposal()
    {
        ServiceContainer container = new ServiceCollection().AddSingleton(new RunLog()).AddScoped<IS<K0>, Leaf<K0>>().BuildLadderServiceProvider();
        IServiceScope scope = container.CreateScope();
        IServiceScope other = container.CreateScope();
        scope.Dispose();

        Assert.Throws<ObjectDisposedException>(() => scope.ServiceProvider.GetService<IS<K0>>());
        Assert.NotNull(other.ServiceProvider.GetService<IS<K0>>()); // scopes are independent

        container.Dispose();
        Assert.Throws<ObjectDisposedException>(() => container.GetService<IS<K0>>());
        Assert.Throws<ObjectDisposedException>(() => other.ServiceProvider.GetService<IS<K0>>()); // the container is gone
        Assert.Throws<ObjectDisposedException>(container.CreateScope);
    }

    [Theory]
    [InlineData(ResolutionMode.Compiled)]
    [InlineData(ResolutionMode.CompiledOnFirstCall)]
    [InlineData(ResolutionMode.Interpreted)]
    public void Concurrent_first_resolutions_share_one_singleton_and_one_scoped_instance_per_scope(ResolutionMode mode)
    {
        IServiceCollection services = new ServiceCollection().AddSingleton(new RunLog())
            .AddSingleton<IS<K0>, Leaf<K0>>()
            .AddScoped<IS<K1>, Dep1<K1, IS<K0>>>()
            .AddTransient<IS<K2>, Dep1<K2, IS<K1>>>();
        using ServiceContainer container = services.BuildLadderServiceProvider(new ServiceContainerOptions { Mode = mode });
        using IServiceScope scope = container.CreateScope();

        var seen = new System.Collections.Concurrent.ConcurrentBag<object>();
        using var start = new Barrier(16);
        Parallel.For(0, 16, new ParallelOptions { MaxDegreeOfParallelism = 16 }, _ =>
        {
            start.SignalAndWait();
            for (int i = 0; i < 100; i++)
            {
                seen.Add(((Node)scope.ServiceProvider.GetRequiredService<IS<K2>>()).Parts[0]!);
            }
        });

        object scoped = Assert.Single(seen.Distinct());
        Assert.Same(container.GetRequiredService<IS<K0>>(), ((Node)scoped).Parts[0]);
    }

    [Fact]
    public void A_scope_grows_to_hold_any_number_of_scoped_services()
    {
        // 200 distinct closed generics, each a scoped service with its own slot, resolved after the scope exists.
        IServiceCollection services = new ServiceCollection().AddSingleton(new RunLog()).AddScoped(typeof(IS<>), typeof(Open<>));
        Type[] closed = [.. Enumerable.Range(0, 200).Select(i => typeof(IS<>).MakeGenericType(
            typeof(ValueTuple<,,>).MakeGenericType(Scenario.Markers[i % 6], Scenario.Markers[i / 6 % 6], Scenario.Markers[i / 36 % 6])))];
        using ServiceContainer container = services.BuildLadderServiceProvider();
        using IServiceScope scope = container.CreateScope();

        object[] first = [.. closed.Select(scope.ServiceProvider.GetRequiredService)];
        object[] second = [.. closed.Select(scope.ServiceProvider.GetRequiredService)];

        Assert.Equal(200, first.Distinct().Count());
        Assert.Equal(first, second);
    }

    /// <summary>
    /// Microsoft's call-site cache is keyed by (service, slot), and a single resolution reads slot 0. Resolving
    /// <c>IEnumerable&lt;T&gt;</c> first puts the last open generic that fits T's constraints into slot 0, so a single
    /// resolution that would otherwise throw (the last open generic doesn't fit) now succeeds. Ours does the same.
    /// </summary>
    [Theory]
    [MemberData(nameof(Containers))]
    public void An_enumeration_can_decide_a_later_single_resolution(string container)
    {
        IServiceCollection services = new ServiceCollection().AddSingleton(new RunLog())
            .AddTransient(typeof(IS<>), typeof(Open<>))
            .AddTransient(typeof(IS<>), typeof(OpenConstrained<>)); // K1 is not IMarked

        Assert.Throws<ArgumentException>(() => Build(container, services).GetService<IS<K1>>());

        IServiceProvider provider = Build(container, services);
        Assert.Single(provider.GetServices<IS<K1>>());
        Assert.IsType<Open<K1>>(provider.GetService<IS<K1>>());
    }

    /// <summary>
    /// An AnyKey enumeration resolves each keyed registration under its own (key, service type). For a keyed open
    /// generic that service type is the open definition, which can't be closed, so it is silently left out, while the
    /// same registration is found by its own key. Microsoft's container behaves this way; ours follows.
    /// </summary>
    [Theory]
    [MemberData(nameof(Containers))]
    public void An_AnyKey_enumeration_leaves_out_keyed_open_generics(string container)
    {
        IServiceCollection services = new ServiceCollection().AddSingleton(new RunLog())
            .AddKeyedTransient(typeof(IS<>), "k1", typeof(Open<>))
            .AddKeyedTransient<IS<K1>, Leaf<K1>>("k2");
        IServiceProvider provider = Build(container, services);

        Assert.IsType<Leaf<K1>>(Assert.Single(provider.GetKeyedServices<IS<K1>>(KeyedService.AnyKey)));
        Assert.IsType<Open<K1>>(Assert.Single(provider.GetKeyedServices<IS<K1>>("k1")));
    }

    /// <summary>
    /// Microsoft's scope validator remembers results by <c>ResultCache.Key</c>, and an instance registration's key is
    /// (unkeyed service type, 0), the same as the unkeyed registration's. Once the unkeyed scoped IS&lt;K1&gt; has been
    /// validated, a singleton that depends on a <em>keyed instance</em> of IS&lt;K1&gt; is refused as a captive
    /// dependency, which it isn't. A false positive, reproduced so both containers refuse the same things.
    /// </summary>
    [Theory]
    [MemberData(nameof(Containers))]
    public void Scope_validation_mistakes_a_keyed_instance_for_the_scoped_registration_once_that_was_validated(string container)
    {
        var log = new RunLog();
        IServiceCollection services = new ServiceCollection().AddSingleton(log)
            .AddScoped<IS<K1>, Leaf<K1>>()
            .AddKeyedSingleton<IS<K1>>("k1", new Leaf<K1>(log))
            .AddSingleton<IS<K3>, KeyedDep<K3, IS<K1>>>();

        Assert.NotNull(Build(container, services, validateScopes: true).GetService<IS<K3>>());

        IServiceProvider provider = Build(container, services, validateScopes: true);
        using (IServiceScope scope = provider.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<IS<K1>>();
        }

        Assert.Contains("Cannot consume scoped service", Assert.Throws<InvalidOperationException>(() => provider.GetService<IS<K3>>()).Message, StringComparison.Ordinal);
    }
}
