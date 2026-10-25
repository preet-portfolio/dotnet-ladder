using System.Diagnostics.CodeAnalysis;

namespace Ladder.DI;

/// <summary>
/// Exception messages. The wording is Microsoft's (<c>Microsoft.Extensions.DependencyInjection/src/Resources/Strings.resx</c>)
/// because callers and tests match on it, and the exception types are part of the contract.
/// </summary>
internal static class Errors
{
    public static InvalidOperationException CircularDependency(string path) => new(path);

    public static InvalidOperationException NoConstructorMatch(Type implementation) =>
        new($"A suitable constructor for type '{implementation}' could not be located. Ensure the type is concrete and services are registered for all parameters of a public constructor.");

    public static InvalidOperationException UnableToActivate(Type implementation) =>
        new($"No constructor for type '{implementation}' can be instantiated using services from the service container and default values.");

    public static InvalidOperationException Ambiguous(Type implementation, object best, object other) =>
        new(string.Join(Environment.NewLine, $"Unable to activate type '{implementation}'. The following constructors are ambiguous:", best, other));

    public static InvalidOperationException CannotResolve(Type parameter, Type implementation) =>
        new($"Unable to resolve service for type '{parameter}' while attempting to activate '{implementation}'.");

    public static InvalidOperationException InvalidServiceKeyType() =>
        new("The type of the key used for lookup doesn't match the type in the constructor parameter with the ServiceKey attribute.");

    public static InvalidOperationException InvalidDescriptor() => new("Invalid service descriptor");

    public static ArgumentException OpenGenericNeedsOpenImplementation(Type service, string paramName) =>
        new($"Open generic service type '{service}' requires registering an open generic implementation type.", paramName);

    public static ArgumentException ArityMismatch(Type service, Type implementation, string paramName) =>
        new($"Arity of open generic service type '{service}' does not equal arity of open generic implementation type '{implementation}'.", paramName);

    public static ArgumentException CannotInstantiate(Type implementation, Type service) =>
        new($"Cannot instantiate implementation type '{implementation}' for service type '{service}'.");

    public static InvalidOperationException ScopedInSingleton(Type scoped, Type singleton) =>
        new($"Cannot consume scoped service '{scoped}' from singleton '{singleton}'.");

    public static InvalidOperationException ScopedFromRoot(Type service, Type scoped) =>
        new($"Cannot resolve '{service}' from root provider because it requires scoped service '{scoped}'.");

    public static InvalidOperationException DirectScopedFromRoot(Type service) =>
        new($"Cannot resolve scoped service '{service}' from root provider.");

    public static InvalidOperationException AsyncDisposableOnly(Type type) =>
        new($"'{type}' type only implements IAsyncDisposable. Use DisposeAsync to dispose the container.");

    [DoesNotReturn]
    public static void ThrowAnyKeyForSingleService() =>
        throw new InvalidOperationException("KeyedService.AnyKey cannot be used to resolve a single service.");

    [DoesNotReturn]
    public static void ThrowNoService(Type type) =>
        throw new InvalidOperationException($"No service for type '{type}' has been registered.");

    [DoesNotReturn]
    public static void ThrowNoKeyedService(Type type, Type keyType) =>
        throw new InvalidOperationException($"No keyed service for type '{type}' using key type '{keyType}' has been registered.");

    [DoesNotReturn]
    public static void ThrowDisposed() => throw new ObjectDisposedException(nameof(IServiceProvider));
}
