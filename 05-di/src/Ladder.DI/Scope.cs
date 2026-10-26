using Ladder.DI.Planning;
using Ladder.DI.Resolution;
using Microsoft.Extensions.DependencyInjection;

namespace Ladder.DI;

/// <summary>
/// One scope: its scoped instances and the disposables it must clean up. The root scope belongs to the container
/// and holds singletons' disposables (the singleton values themselves live on their plans). Scopes are flat, not
/// nested: a scope created from another scope is independent of it, and disposing the outer one leaves it alone.
/// </summary>
internal sealed class Scope : IServiceScope, IKeyedServiceProvider, IServiceScopeFactory, IAsyncDisposable
{
    /// <summary>Marks "resolved, and the factory returned null" in a slot, so null results are cached too.</summary>
    private static readonly object NullResult = new();

    private readonly ServiceContainer _container;
    private readonly Lock _sync = new();
    private object?[] _scoped = [];
    private List<object>? _disposables;
    private bool _disposed;

    public Scope(ServiceContainer container, bool isRoot)
    {
        _container = container;
        IsRoot = isRoot;
    }

    public bool IsRoot { get; }

    public Scope Root => _container.Root;

    public IServiceProvider ServiceProvider => this;

    internal bool IsDisposed => Volatile.Read(ref _disposed);

    public object? GetService(Type serviceType)
    {
        if (IsDisposed)
        {
            Errors.ThrowDisposed();
        }

        return _container.Resolve(serviceType, this);
    }

    public object? GetKeyedService(Type serviceType, object? serviceKey)
    {
        if (IsDisposed)
        {
            Errors.ThrowDisposed();
        }

        return _container.ResolveKeyed(serviceType, serviceKey, this);
    }

    public object GetRequiredKeyedService(Type serviceType, object? serviceKey)
    {
        if (IsDisposed)
        {
            Errors.ThrowDisposed();
        }

        return _container.ResolveRequiredKeyed(serviceType, serviceKey, this);
    }

    public IServiceScope CreateScope() => _container.CreateScope();

    /// <summary>
    /// The scope's instance of a scoped plan, created on first use. Reads are lock-free: a slot is written once,
    /// under the lock, with a release store, so a reader either sees the finished instance or takes the lock.
    /// </summary>
    public object? GetOrCreateScoped(Plan plan)
    {
        if (IsRoot)
        {
            // A scoped service resolved from the root lives as long as the root, exactly like a singleton.
            return Interpreter.ResolveRoot(plan, this);
        }

        int slot = plan.ScopeSlot;
        object?[] slots = Volatile.Read(ref _scoped);
        if ((uint)slot < (uint)slots.Length && Volatile.Read(ref slots[slot]) is { } existing)
        {
            return ReferenceEquals(existing, NullResult) ? null : existing;
        }

        lock (_sync)
        {
            slots = _scoped;
            if ((uint)slot < (uint)slots.Length && slots[slot] is { } raced)
            {
                return ReferenceEquals(raced, NullResult) ? null : raced;
            }

            object? value = plan.CompiledBody is { } body ? body(this) : Interpreter.Create(plan, this);
            Capture(value);

            // Creating the value may have created other scoped services and grown the array, so look again.
            slots = _scoped;
            if (slot >= slots.Length)
            {
                var grown = new object?[Math.Max(Math.Max(slot + 1, _container.Planner.ScopeSlotCount), slots.Length * 2)];
                slots.CopyTo(grown, 0);
                Volatile.Write(ref _scoped, grown);
                slots = grown;
            }

            Volatile.Write(ref slots[slot], value ?? NullResult);
            return value;
        }
    }

    /// <summary>
    /// Registers a service this scope created for disposal. Order of capture is order of creation finishing, so a
    /// service's dependencies are captured before it, and the reverse walk in Dispose tears dependents down first.
    /// </summary>
    public object? Capture(object? service)
    {
        if (service is null || ReferenceEquals(service, this) || service is not (IDisposable or IAsyncDisposable))
        {
            return service;
        }

        bool disposed;
        lock (_sync)
        {
            disposed = _disposed;
            if (!disposed)
            {
                (_disposables ??= []).Add(service);
            }
        }

        if (disposed)
        {
            // The scope was disposed while this was being created: clean it up rather than leak it, then fail.
            if (service is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else
            {
                Task.Run(() => ((IAsyncDisposable)service).DisposeAsync().AsTask()).GetAwaiter().GetResult();
            }

            Errors.ThrowDisposed();
        }

        return service;
    }

    public void Dispose()
    {
        List<object>? toDispose = BeginDispose();
        if (toDispose is null)
        {
            return;
        }

        for (int i = toDispose.Count - 1; i >= 0; i--)
        {
            if (toDispose[i] is IDisposable disposable)
            {
                disposable.Dispose();
            }
            else
            {
                // Sync disposal can't honour an IAsyncDisposable-only service; say so instead of blocking on it.
                throw Errors.AsyncDisposableOnly(toDispose[i].GetType());
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        List<object>? toDispose = BeginDispose();
        if (toDispose is null)
        {
            return;
        }

        for (int i = toDispose.Count - 1; i >= 0; i--)
        {
            if (toDispose[i] is IAsyncDisposable asyncDisposable)
            {
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            }
            else
            {
                ((IDisposable)toDispose[i]).Dispose();
            }
        }
    }

    private List<object>? BeginDispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return null;
            }

            // From here on Capture disposes anything new immediately; the list no longer changes.
            _disposed = true;
        }

        if (IsRoot)
        {
            _container.MarkDisposed();
        }

        return _disposables;
    }
}
