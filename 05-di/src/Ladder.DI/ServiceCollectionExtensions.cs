using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI;

public static class ServiceCollectionExtensions
{
    /// <summary>Builds a <see cref="ServiceContainer"/>; the counterpart of <c>BuildServiceProvider()</c>.</summary>
    public static ServiceContainer BuildLadderServiceProvider(this IServiceCollection services, ServiceContainerOptions? options = null) =>
        new(services, options ?? new ServiceContainerOptions());
}
