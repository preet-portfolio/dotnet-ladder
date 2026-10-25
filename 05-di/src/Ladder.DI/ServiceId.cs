using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI;

/// <summary>
/// What a caller asks for: a service type plus an optional key. A null key means "not keyed", which is how the
/// contract lets <c>GetKeyedService(type, null)</c> and <c>GetService(type)</c> mean the same thing.
/// Equality follows <c>ServiceLookup/ServiceIdentifier.cs</c>: keys compare with <see cref="object.Equals(object)"/>.
/// </summary>
internal readonly struct ServiceId(object? key, Type type) : IEquatable<ServiceId>
{
    public object? Key { get; } = key;

    public Type Type { get; } = type;

    public static ServiceId Of(Type type) => new(null, type);

    public static ServiceId Of(ServiceDescriptor descriptor) => new(descriptor.ServiceKey, descriptor.ServiceType);

    public ServiceId GenericDefinition() => new(Key, Type.GetGenericTypeDefinition());

    public bool Equals(ServiceId other) =>
        Type == other.Type && (Key is null ? other.Key is null : other.Key is not null && Key.Equals(other.Key));

    public override bool Equals(object? obj) => obj is ServiceId other && Equals(other);

    public override int GetHashCode() => Key is null ? Type.GetHashCode() : HashCode.Combine(Type, Key);

    public override string ToString() => Key is null ? Type.ToString() : $"({Key}, {Type})";
}

/// <summary>
/// The identity of a plan, and so of the singleton or scoped instance it produces: the service asked for, plus a slot
/// that counts registrations of it from the last one (slot 0) backwards. A single resolution is slot 0, so
/// <c>GetService&lt;T&gt;()</c> and the last element of <c>IEnumerable&lt;T&gt;</c> are the same object. This is
/// Microsoft's <c>ServiceCacheKey</c>. Keying by registration instead looks equivalent but isn't; see
/// <c>Planner.TryEnumerable</c>.
/// </summary>
internal readonly record struct InstanceKey(ServiceId Service, int Slot);
