using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI.Tests;

/// <summary>
/// Claim 3: once warm, resolving a singleton or a scoped service allocates nothing, and resolving a (non-disposable)
/// transient allocates exactly what <c>new</c> would. Measured with the thread's allocation counter, which is exact.
/// </summary>
public class AllocationTests
{
    private const int Iterations = 1_000;

    [Theory]
    [InlineData(ResolutionMode.Compiled)]
    [InlineData(ResolutionMode.Interpreted)]
    public void A_warm_singleton_allocates_nothing_from_the_container_or_a_scope(ResolutionMode mode)
    {
        using ServiceContainer container = Build(mode);
        using IServiceScope scope = container.CreateScope();

        Assert.Equal(0, BytesPerCall(() => container.GetService(typeof(Leaf))));
        Assert.Equal(0, BytesPerCall(() => scope.ServiceProvider.GetService(typeof(Leaf))));
    }

    [Theory]
    [InlineData(ResolutionMode.Compiled)]
    [InlineData(ResolutionMode.Interpreted)]
    public void A_warm_scoped_service_allocates_nothing(ResolutionMode mode)
    {
        using ServiceContainer container = Build(mode);
        using IServiceScope scope = container.CreateScope();

        Assert.Equal(0, BytesPerCall(() => scope.ServiceProvider.GetService(typeof(ScopedThing))));
    }

    [Fact]
    public void A_warm_transient_graph_allocates_exactly_what_new_allocates()
    {
        using ServiceContainer container = Build(ResolutionMode.Compiled);
        using IServiceScope scope = container.CreateScope();

        double byHand = BytesPerCall(() => new Top(new Middle(new Bottom()), new Bottom()));
        Assert.True(byHand > 0);
        Assert.Equal(byHand, BytesPerCall(() => container.GetService(typeof(Top))));
        Assert.Equal(byHand, BytesPerCall(() => scope.ServiceProvider.GetService(typeof(Top))));
    }

    [Fact]
    public void The_interpreter_pays_for_reflection_on_every_transient()
    {
        // Not a claim, the reason compilation exists: argument arrays per constructor call.
        using ServiceContainer container = Build(ResolutionMode.Interpreted);
        double byHand = BytesPerCall(() => new Top(new Middle(new Bottom()), new Bottom()));
        Assert.True(BytesPerCall(() => container.GetService(typeof(Top))) > byHand);
    }

    private static ServiceContainer Build(ResolutionMode mode) => new ServiceCollection()
        .AddSingleton<Leaf>()
        .AddScoped<ScopedThing>()
        .AddTransient<Bottom>()
        .AddTransient<Middle>()
        .AddTransient<Top>()
        .BuildLadderServiceProvider(new ServiceContainerOptions { Mode = mode });

    private static double BytesPerCall(Func<object?> resolve)
    {
        // Warm up: the first calls build plans, compile delegates and JIT; none of that is the steady state.
        for (int i = 0; i < 100; i++)
        {
            GC.KeepAlive(resolve());
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < Iterations; i++)
        {
            GC.KeepAlive(resolve());
        }

        return (GC.GetAllocatedBytesForCurrentThread() - before) / (double)Iterations;
    }

    private sealed class Leaf;

    private sealed class ScopedThing(Leaf leaf)
    {
        public Leaf Leaf { get; } = leaf;
    }

    private sealed class Bottom;

    private sealed class Middle(Bottom bottom)
    {
        public Bottom Bottom { get; } = bottom;
    }

    private sealed class Top(Middle middle, Bottom bottom)
    {
        public Middle Middle { get; } = middle;

        public Bottom Bottom { get; } = bottom;
    }
}
