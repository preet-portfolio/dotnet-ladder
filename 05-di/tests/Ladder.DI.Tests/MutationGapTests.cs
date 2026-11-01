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
