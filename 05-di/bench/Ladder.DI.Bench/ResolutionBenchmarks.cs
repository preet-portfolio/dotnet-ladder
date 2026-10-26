using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI.Bench;

/// <summary>
/// Claims 3 and 4: warm resolution time and allocation, ours (compiled, and interpreted for comparison) against
/// Microsoft's ServiceProvider, which compiles hot services to IL on a background thread. Each category's baseline is
/// Microsoft's container, so the Ratio column is the claim. Setup resolves every service a few times and waits, so
/// both containers are measured after compilation.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class ResolutionBenchmarks
{
    private readonly Singleton _singleton = new();
    private IServiceProvider _microsoft = null!;
    private IServiceProvider _ours = null!;
    private IServiceProvider _interpreted = null!;
    private IServiceScope _microsoftScope = null!;
    private IServiceScope _oursScope = null!;
    private IServiceScope _interpretedScope = null!;

    [GlobalSetup]
    public void Setup()
    {
        _microsoft = Services().BuildServiceProvider();
        _ours = Services().BuildLadderServiceProvider();
        _interpreted = Services().BuildLadderServiceProvider(new ServiceContainerOptions { Mode = ResolutionMode.Interpreted });
        _microsoftScope = _microsoft.CreateScope();
        _oursScope = _ours.CreateScope();
        _interpretedScope = _interpreted.CreateScope();

        foreach (IServiceProvider provider in new[] { _microsoftScope.ServiceProvider, _oursScope.ServiceProvider, _interpretedScope.ServiceProvider })
        {
            for (int i = 0; i < 10; i++)
            {
                provider.GetService(typeof(Singleton));
                provider.GetService(typeof(Scoped));
                provider.GetService(typeof(Transient));
                provider.GetService(typeof(Top));
            }
        }

        // Microsoft's container compiles on the thread pool after the second call; give it time to swap in the result.
        Thread.Sleep(500);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (IDisposable disposable in new IDisposable[] { _microsoftScope, _oursScope, _interpretedScope, (IDisposable)_microsoft, (IDisposable)_ours, (IDisposable)_interpreted })
        {
            disposable.Dispose();
        }
    }

    [Benchmark(Baseline = true), BenchmarkCategory("Singleton")]
    public object? Singleton_Microsoft() => _microsoftScope.ServiceProvider.GetService(typeof(Singleton));

    [Benchmark, BenchmarkCategory("Singleton")]
    public object? Singleton_Ours() => _oursScope.ServiceProvider.GetService(typeof(Singleton));

    [Benchmark, BenchmarkCategory("Singleton")]
    public object? Singleton_Interpreted() => _interpretedScope.ServiceProvider.GetService(typeof(Singleton));

    [Benchmark(Baseline = true), BenchmarkCategory("Scoped")]
    public object? Scoped_Microsoft() => _microsoftScope.ServiceProvider.GetService(typeof(Scoped));

    [Benchmark, BenchmarkCategory("Scoped")]
    public object? Scoped_Ours() => _oursScope.ServiceProvider.GetService(typeof(Scoped));

    [Benchmark, BenchmarkCategory("Scoped")]
    public object? Scoped_Interpreted() => _interpretedScope.ServiceProvider.GetService(typeof(Scoped));

    [Benchmark(Baseline = true), BenchmarkCategory("Transient")]
    public object? Transient_Microsoft() => _microsoftScope.ServiceProvider.GetService(typeof(Transient));

    [Benchmark, BenchmarkCategory("Transient")]
    public object? Transient_Ours() => _oursScope.ServiceProvider.GetService(typeof(Transient));

    [Benchmark, BenchmarkCategory("Transient")]
    public object? Transient_Interpreted() => _interpretedScope.ServiceProvider.GetService(typeof(Transient));

    [Benchmark(Baseline = true), BenchmarkCategory("Graph")]
    public object? Graph_Microsoft() => _microsoftScope.ServiceProvider.GetService(typeof(Top));

    [Benchmark, BenchmarkCategory("Graph")]
    public object? Graph_Ours() => _oursScope.ServiceProvider.GetService(typeof(Top));

    [Benchmark, BenchmarkCategory("Graph")]
    public object? Graph_Interpreted() => _interpretedScope.ServiceProvider.GetService(typeof(Top));

    /// <summary>The same graph built by hand: the floor any container is measured against.</summary>
    [Benchmark, BenchmarkCategory("Graph")]
    public object Graph_New() => new Top(new Middle(new Bottom(), _singleton), new Bottom());

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton<Singleton>();
        services.AddScoped<Scoped>();
        services.AddTransient<Transient>();
        services.AddTransient<Bottom>();
        services.AddTransient<Middle>();
        services.AddTransient<Top>();
        return services;
    }

    public sealed class Singleton;

    public sealed class Scoped(Singleton singleton)
    {
        public Singleton Singleton { get; } = singleton;
    }

    public sealed class Transient;

    public sealed class Bottom;

    public sealed class Middle(Bottom bottom, Singleton singleton)
    {
        public Bottom Bottom { get; } = bottom;

        public Singleton Singleton { get; } = singleton;
    }

    /// <summary>Three levels: Top → Middle → Bottom, plus a second Bottom and a singleton inside Middle.</summary>
    public sealed class Top(Middle middle, Bottom bottom)
    {
        public Middle Middle { get; } = middle;

        public Bottom Bottom { get; } = bottom;
    }
}
