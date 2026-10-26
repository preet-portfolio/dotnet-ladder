using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Specification;

namespace Ladder.DI.Judge;

// Every test class the package ships is abstract with one factory method; each subject below runs the whole class
// against one container. The four variants: Microsoft's container (the reference), and ours with each resolution
// engine, since most spec tests resolve a service only once or twice and would otherwise never reach the compiler.
// DependencyInjectionSpecificationTests includes the ActivatorUtilities and IServiceProviderIsService partials;
// KeyedDependencyInjectionSpecificationTests includes the IServiceProviderIsKeyedService partial.

public class Reference_Specification : DependencyInjectionSpecificationTests
{
    protected override IServiceProvider CreateServiceProvider(IServiceCollection serviceCollection) => serviceCollection.BuildServiceProvider();
}

public class Reference_Keyed : KeyedDependencyInjectionSpecificationTests
{
    protected override IServiceProvider CreateServiceProvider(IServiceCollection collection) => collection.BuildServiceProvider();
}

public class Ours_Specification : DependencyInjectionSpecificationTests
{
    protected override IServiceProvider CreateServiceProvider(IServiceCollection serviceCollection) => serviceCollection.BuildLadderServiceProvider();
}

public class Ours_Keyed : KeyedDependencyInjectionSpecificationTests
{
    protected override IServiceProvider CreateServiceProvider(IServiceCollection collection) => collection.BuildLadderServiceProvider();
}

public class OursCompiledOnFirstCall_Specification : DependencyInjectionSpecificationTests
{
    protected override IServiceProvider CreateServiceProvider(IServiceCollection serviceCollection) =>
        serviceCollection.BuildLadderServiceProvider(new ServiceContainerOptions { Mode = ResolutionMode.CompiledOnFirstCall });
}

public class OursCompiledOnFirstCall_Keyed : KeyedDependencyInjectionSpecificationTests
{
    protected override IServiceProvider CreateServiceProvider(IServiceCollection collection) =>
        collection.BuildLadderServiceProvider(new ServiceContainerOptions { Mode = ResolutionMode.CompiledOnFirstCall });
}

public class OursInterpreted_Specification : DependencyInjectionSpecificationTests
{
    protected override IServiceProvider CreateServiceProvider(IServiceCollection serviceCollection) =>
        serviceCollection.BuildLadderServiceProvider(new ServiceContainerOptions { Mode = ResolutionMode.Interpreted });
}

public class OursInterpreted_Keyed : KeyedDependencyInjectionSpecificationTests
{
    protected override IServiceProvider CreateServiceProvider(IServiceCollection collection) =>
        collection.BuildLadderServiceProvider(new ServiceContainerOptions { Mode = ResolutionMode.Interpreted });
}
