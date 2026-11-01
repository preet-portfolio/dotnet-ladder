using Ladder.DI.Tests.Graph;
using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI.Tests;

/// <summary>
/// Tests written for the mutants that survived both the unit tests and the specification suite (05-di/evidence/stryker.md).
/// Wherever Microsoft's container has a defined answer, the test asks both containers and requires the same exception
/// type and message, since our messages copy Microsoft's on purpose.
/// </summary>
public class MutationGapTests
{
    private static ServiceContainer Ours(IServiceCollection services, bool validateScopes = false, bool validateOnBuild = false) =>
        services.BuildLadderServiceProvider(new ServiceContainerOptions { ValidateScopes = validateScopes, ValidateOnBuild = validateOnBuild });

    private static ServiceProvider Microsoft(IServiceCollection services, bool validateScopes = false, bool validateOnBuild = false) =>
        services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = validateScopes, ValidateOnBuild = validateOnBuild });

    private static (Type Type, string Message) Failure(Action action)
    {
        Exception e = Assert.ThrowsAny<Exception>(action);
        return (e.GetType(), e.Message);
    }

    private static void SameFailure(Func<IServiceCollection> services, Action<IServiceProvider> act, bool validateScopes = false)
    {
        (Type Type, string Message) expected = Failure(() => act(Microsoft(services(), validateScopes)));
        (Type Type, string Message) actual = Failure(() => act(Ours(services(), validateScopes)));
        Assert.Equal(expected, actual);
    }

    // --- Registration errors, seen when the container is built (Planner.Validate) ---------------------------------

    public static TheoryData<string> BadRegistrations => ["open service, closed implementation", "open service, abstract open implementation",
        "open service, interface implementation", "arity mismatch", "closed service, abstract implementation", "closed service, open implementation",
        "open service, factory"];

    private static ServiceCollection BadRegistration(string name)
    {
        var services = new ServiceCollection();
        ((ICollection<ServiceDescriptor>)services).Add(name switch
        {
            "open service, closed implementation" => new ServiceDescriptor(typeof(IOpen<>), typeof(Closed), ServiceLifetime.Transient),
            "open service, abstract open implementation" => new ServiceDescriptor(typeof(IOpen<>), typeof(AbstractOpen<>), ServiceLifetime.Transient),
            "open service, interface implementation" => new ServiceDescriptor(typeof(IOpen<>), typeof(IOpen<>), ServiceLifetime.Transient),
            "arity mismatch" => new ServiceDescriptor(typeof(IOpen<>), typeof(TwoArgs<,>), ServiceLifetime.Transient),
            "closed service, abstract implementation" => new ServiceDescriptor(typeof(IClosed), typeof(AbstractClosed), ServiceLifetime.Transient),
            "closed service, open implementation" => new ServiceDescriptor(typeof(IClosed), typeof(Open<>), ServiceLifetime.Transient),
            _ => new ServiceDescriptor(typeof(IOpen<>), _ => new Closed(), ServiceLifetime.Transient),
        });
        return services;
    }

    [Theory]
    [MemberData(nameof(BadRegistrations))]
    public void A_bad_registration_fails_at_build_exactly_as_Microsofts_does(string name)
    {
        (Type Type, string Message) expected = Failure(() => Microsoft(BadRegistration(name)));
        (Type Type, string Message) actual = Failure(() => Ours(BadRegistration(name)));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Null_arguments_are_rejected_with_ArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new ServiceContainer(null!));
        Assert.Throws<ArgumentNullException>(() => new ServiceContainer(new ServiceCollection(), null!));
        var isService = Ours(new ServiceCollection()).GetRequiredService<IServiceProviderIsService>();
        Assert.Throws<ArgumentNullException>(() => isService.IsService(null!));
    }

    // --- IServiceProviderIsService and IServiceProviderIsKeyedService ---------------------------------------------

    [Fact]
    public void The_IsService_built_ins_are_resolvable_and_report_themselves()
    {
        foreach (IServiceProvider provider in new IServiceProvider[] { Microsoft(new ServiceCollection()), Ours(new ServiceCollection()) })
        {
            var isService = provider.GetRequiredService<IServiceProviderIsService>();
            var isKeyed = provider.GetRequiredService<IServiceProviderIsKeyedService>();
            Assert.True(isService.IsService(typeof(IServiceProviderIsService)));
            Assert.True(isService.IsService(typeof(IServiceProviderIsKeyedService)));
            Assert.True(isService.IsService(typeof(IServiceProvider)));
            Assert.True(isService.IsService(typeof(IServiceScopeFactory)));
            Assert.False(isService.IsService(typeof(IClosed)));
            Assert.True(isKeyed.IsKeyedService(typeof(IServiceProviderIsService), null));
        }
    }

    public static TheoryData<string> IsServiceQueries => ["unkeyed, only AnyKey registered", "keyed, only AnyKey registered", "keyed, other key registered",
        "open definition", "closed generic of registered open", "IEnumerable of unregistered"];

    [Theory]
    [MemberData(nameof(IsServiceQueries))]
    public void IsService_answers_like_Microsofts(string query)
    {
        static ServiceCollection Services()
        {
            var services = new ServiceCollection();
            services.AddKeyedTransient<IClosed, ClosedService>(KeyedService.AnyKey);
            services.AddKeyedTransient<IOther, OtherService>("a");
            services.AddTransient(typeof(IOpen<>), typeof(Open<>));
            return services;
        }

        bool Ask(IServiceProvider provider)
        {
            var isKeyed = provider.GetRequiredService<IServiceProviderIsKeyedService>();
            return query switch
            {
                "unkeyed, only AnyKey registered" => isKeyed.IsService(typeof(IClosed)),
                "keyed, only AnyKey registered" => isKeyed.IsKeyedService(typeof(IClosed), "x"),
                "keyed, other key registered" => isKeyed.IsKeyedService(typeof(IOther), "b"),
                "open definition" => isKeyed.IsService(typeof(IOpen<>)),
                "closed generic of registered open" => isKeyed.IsService(typeof(IOpen<int>)),
                _ => isKeyed.IsService(typeof(IEnumerable<IOther>)),
            };
        }

        Assert.Equal(Ask(Microsoft(Services())), Ask(Ours(Services())));
    }

    // --- Error messages and the type names inside them ------------------------------------------------------------

    [Fact]
    public void A_type_without_a_public_constructor_fails_like_Microsofts()
    {
        SameFailure(() => new ServiceCollection().AddTransient<NoPublicConstructor>(), p => p.GetService<NoPublicConstructor>());
    }

    [Fact]
    public void A_missing_required_service_fails_like_Microsofts()
    {
        SameFailure(() => new ServiceCollection(), p => p.GetRequiredService<IClosed>());
        SameFailure(() => new ServiceCollection(), p => p.GetRequiredKeyedService<IClosed>("k"));
    }

    [Fact]
    public void Resolving_scoped_services_from_the_root_fails_like_Microsofts()
    {
        static IServiceCollection Services() => new ServiceCollection().AddScoped<IClosed, ClosedService>().AddTransient<NeedsClosed>();
        SameFailure(Services, p => p.GetService<IClosed>(), validateScopes: true);      // directly
        SameFailure(Services, p => p.GetService<NeedsClosed>(), validateScopes: true);  // through a dependency
    }

    [Fact]
    public void ValidateOnBuild_aggregates_like_Microsofts()
    {
        static IServiceCollection Services() => new ServiceCollection().AddSingleton<NeedsClosed>();
        (Type Type, string Message) expected = Failure(() => Microsoft(Services(), validateOnBuild: true));
        (Type Type, string Message) actual = Failure(() => Ours(Services(), validateOnBuild: true));
        Assert.Equal(expected, actual);
    }

    public static TheoryData<string> CycleShapes => ["keywords", "arrays", "nested generic"];

    [Theory]
    [MemberData(nameof(CycleShapes))]
    public void A_circular_dependency_message_names_types_like_Microsofts(string shape)
    {
        // The cycle runs through a generic service whose type arguments exercise TypeNames: C# keywords, arrays of
        // several ranks, and a generic type nested in a generic type.
        Type through = shape switch
        {
            "keywords" => typeof(IThrough<(bool, byte, char, decimal, double, float, int, long, object, sbyte, short, string, uint, ulong, ushort)>),
            "arrays" => typeof(IThrough<int[][,]>),
            _ => typeof(IThrough<Outer<int>.Inner<string>>),
        };
        IServiceCollection Services() => new ServiceCollection()
            .AddTransient(typeof(IThrough<>), typeof(Through<>))
            .AddTransient(typeof(Back<>), typeof(Back<>))
            .AddTransient(typeof(IOpen<>), typeof(OpenNeedingThrough<>));
        Type start = through.GetGenericArguments()[0];
        SameFailure(Services, p => p.GetService(typeof(IThrough<>).MakeGenericType(start)));
    }

    // --- Scope slot growth (Scope.GetOrCreateScoped) --------------------------------------------------------------

    [Theory]
    [InlineData(ResolutionMode.Compiled)]
    [InlineData(ResolutionMode.Interpreted)]
    public void Scoped_services_first_planned_after_a_scope_exists_keep_one_instance_per_scope(ResolutionMode mode)
    {
        var services = new ServiceCollection();
        Type[] types = [typeof(Slot<K0>), typeof(Slot<K1>), typeof(Slot<K2>), typeof(Slot<K3>), typeof(Slot<K4>), typeof(Slot<K5>),
            typeof(Slot<Slot<K0>>), typeof(Slot<Slot<K1>>), typeof(Slot<Slot<K2>>), typeof(Slot<Slot<K3>>), typeof(Slot<Slot<K4>>)];
        foreach (Type t in types)
        {
            services.AddScoped(t);
        }

        using ServiceContainer container = services.BuildLadderServiceProvider(new ServiceContainerOptions { Mode = mode });
        using IServiceScope scope = container.CreateScope(); // created before any scoped plan exists, so its slot array starts empty
        using IServiceScope other = container.CreateScope();

        // Resolve in reverse so later slots are claimed before earlier ones are written.
        object[] first = [.. types.Reverse().Select(t => scope.ServiceProvider.GetRequiredService(t))];
        object[] again = [.. types.Reverse().Select(t => scope.ServiceProvider.GetRequiredService(t))];
        object[] elsewhere = [.. types.Reverse().Select(t => other.ServiceProvider.GetRequiredService(t))];
        for (int i = 0; i < types.Length; i++)
        {
            Assert.Same(first[i], again[i]);
            Assert.NotSame(first[i], elsewhere[i]);
        }
    }

    // --- ServiceId equality ---------------------------------------------------------------------------------------

    [Fact]
    public void ServiceId_equality_needs_both_type_and_key_to_match()
    {
        Assert.NotEqual(new ServiceId(null, typeof(IClosed)), new ServiceId(null, typeof(IOther)));
        Assert.NotEqual(new ServiceId("a", typeof(IClosed)), new ServiceId("b", typeof(IClosed)));
        Assert.NotEqual(new ServiceId("a", typeof(IClosed)), new ServiceId(null, typeof(IClosed)));
        Assert.NotEqual(new ServiceId(null, typeof(IClosed)), new ServiceId("a", typeof(IClosed)));
        Assert.Equal(new ServiceId("a", typeof(IClosed)), new ServiceId(new string('a', 1), typeof(IClosed)));
        Assert.Equal(new ServiceId("a", typeof(IClosed)).GetHashCode(), new ServiceId(new string('a', 1), typeof(IClosed)).GetHashCode());
        Assert.Equal("Ladder.DI.Tests.MutationGapTests+IClosed", new ServiceId(null, typeof(IClosed)).ToString());
        Assert.Equal("(a, Ladder.DI.Tests.MutationGapTests+IClosed)", new ServiceId("a", typeof(IClosed)).ToString());
    }

    // --- Second round: survivors of the first round (see 05-di/evidence/stryker.md) -------------------------------

    [Fact]
    public void A_missing_required_service_asked_for_with_a_null_key_fails_like_Microsofts()
    {
        SameFailure(() => new ServiceCollection(), p => p.GetRequiredKeyedService<IClosed>(null));
    }

    [Theory]
    [InlineData(ResolutionMode.Compiled)]
    [InlineData(ResolutionMode.Interpreted)]
    public void A_disposable_transient_made_by_a_factory_is_disposed_with_its_scope(ResolutionMode mode)
    {
        var services = new ServiceCollection().AddTransient(_ => new Tracker());
        using ServiceContainer container = services.BuildLadderServiceProvider(new ServiceContainerOptions { Mode = mode });
        var made = new List<Tracker>();
        using (IServiceScope scope = container.CreateScope())
        {
            for (int i = 0; i < 3; i++) // three calls, so the compiled engine is reached on the second
            {
                made.Add(scope.ServiceProvider.GetRequiredService<Tracker>());
            }
        }

        Assert.All(made, t => Assert.True(t.Disposed));
    }

    [Fact]
    public void An_open_generic_IEnumerable_registration_is_used_like_Microsofts()
    {
        static IServiceCollection Services() => new ServiceCollection()
            .AddTransient<IClosed, ClosedService>()
            .AddTransient(typeof(IEnumerable<>), typeof(CustomEnumerable<>));
        Assert.Equal(Microsoft(Services()).GetService<IEnumerable<IClosed>>()?.GetType(), Ours(Services()).GetService<IEnumerable<IClosed>>()?.GetType());
    }

    [Fact]
    public void Keyed_enumerations_share_instances_with_single_resolutions_like_Microsofts()
    {
        // Singletons under a specific key, under another key, and under AnyKey. Which enumerated instances are the
        // same objects as single resolutions depends on the slot each registration gets; the pattern must match.
        static IServiceCollection Services() => new ServiceCollection()
            .AddKeyedSingleton<IClosed, ClosedService>("a")
            .AddKeyedSingleton<IClosed, ClosedService>("a")
            .AddKeyedSingleton<IClosed, ClosedService>("b")
            .AddKeyedSingleton<IClosed, ClosedService>(KeyedService.AnyKey);

        static bool[] Pattern(IServiceProvider p)
        {
            object[] any = [.. p.GetKeyedServices<IClosed>(KeyedService.AnyKey)];
            object[] a = [.. p.GetKeyedServices<IClosed>("a")];
            object[] x = [.. p.GetKeyedServices<IClosed>("x")];
            object[] singles = [p.GetRequiredKeyedService<IClosed>("a"), p.GetRequiredKeyedService<IClosed>("b"), p.GetRequiredKeyedService<IClosed>("x")];
            object[] all = [.. any, .. a, .. x, .. singles];
            return [.. from i in all from j in all select ReferenceEquals(i, j), .. new[] { any.Length == 3, a.Length == 2, x.Length == 1 }];
        }

        Assert.Equal(Pattern(Microsoft(Services())), Pattern(Ours(Services())));
    }

    [Fact]
    public void Default_parameter_values_are_supplied_like_Microsofts()
    {
        static string Describe(IServiceProvider p)
        {
            var d = p.GetRequiredService<WithDefaults>();
            return $"{d.Point.X},{d.Point.Y}|{d.Mode}|{d.Count}|{d.Name ?? "null"}";
        }

        static IServiceCollection Services() => new ServiceCollection().AddTransient<WithDefaults>();
        Assert.Equal(Describe(Microsoft(Services())), Describe(Ours(Services())));
    }

    [Fact]
    public void A_ServiceKey_parameter_under_AnyKey_gets_the_key_like_Microsofts()
    {
        static IServiceCollection Services() => new ServiceCollection().AddKeyedTransient<KeyTaker>(KeyedService.AnyKey);
        static string Describe(IServiceProvider p) =>
            string.Join(",", p.GetKeyedServices<KeyTaker>(KeyedService.AnyKey).Select(k => k.Key?.ToString() ?? "null"))
            + "|" + (p.GetKeyedService<KeyTaker>("z")?.Key?.ToString() ?? "null");
        Assert.Equal(Describe(Microsoft(Services())), Describe(Ours(Services())));
    }

    [Fact]
    public void Keyed_resolution_after_the_container_is_disposed_fails_like_Microsofts()
    {
        static IServiceCollection Services() => new ServiceCollection().AddKeyedTransient<IClosed, ClosedService>("k");
        SameFailure(Services, p =>
        {
            ((IDisposable)p).Dispose();
            p.GetKeyedService<IClosed>("k");
        });
    }

    public sealed class Tracker : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    public sealed class CustomEnumerable<T> : List<T>;

    public readonly record struct Point(int X, int Y);

    public enum Mode
    {
        First,
        Second,
    }

    public sealed class WithDefaults(Point point = default, Mode? mode = Mode.Second, int count = 7, string? name = null)
    {
        public Point Point { get; } = point;

        public Mode? Mode { get; } = mode;

        public int Count { get; } = count;

        public string? Name { get; } = name;
    }

    public sealed class KeyTaker([ServiceKey] object? key)
    {
        public object? Key { get; } = key;
    }

    public interface IOpen<T>;

    public interface IClosed;

    public interface IOther;

    public interface IThrough<T>;

    public sealed class Closed;

    public sealed class ClosedService : IClosed;

    public sealed class OtherService : IOther;

    public abstract class AbstractOpen<T> : IOpen<T>;

    public sealed class TwoArgs<T, U> : IOpen<T>;

    public abstract class AbstractClosed : IClosed;

    public sealed class Open<T> : IOpen<T>, IClosed;

    public sealed class NeedsClosed(IClosed closed)
    {
        public IClosed Closed { get; } = closed;
    }

    public sealed class NoPublicConstructor
    {
        private NoPublicConstructor()
        {
        }
    }

    public sealed class Through<T>(Back<T> back) : IThrough<T>
    {
        public Back<T> Back { get; } = back;
    }

    public sealed class Back<T>(IOpen<T> open)
    {
        public IOpen<T> Open { get; } = open;
    }

    public sealed class OpenNeedingThrough<T>(IThrough<T> through) : IOpen<T>
    {
        public IThrough<T> Through { get; } = through;
    }

    public sealed class Slot<T>;

    public sealed class Outer<T>
    {
        public sealed class Inner<U>;
    }
}
