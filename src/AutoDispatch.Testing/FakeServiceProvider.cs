using System;
using System.Collections.Generic;

namespace AutoDispatch.Testing;

/// <summary>
/// A minimal <see cref="IServiceProvider"/> test double for unit-testing AutoDispatch handlers
/// and the generated <c>Dispatcher</c>/<c>AddAutoDispatch()</c> wiring without spinning up a real
/// <c>Microsoft.Extensions.DependencyInjection</c> container.
/// </summary>
/// <example>
/// <code>
/// var sp = new FakeServiceProvider()
///     .Add&lt;CreateOrderHandler&gt;(new CreateOrderHandler())
///     .Add&lt;LoggingBehavior&lt;CreateOrderCommand, OrderId&gt;&gt;(new LoggingBehavior&lt;CreateOrderCommand, OrderId&gt;());
///
/// IDispatcher dispatcher = new Dispatcher(sp);
/// </code>
/// </example>
public sealed class FakeServiceProvider : IServiceProvider
{
    private readonly Dictionary<Type, object> _services = new();

    /// <summary>Registers an instance to be returned for <typeparamref name="TService"/>.</summary>
    public FakeServiceProvider Add<TService>(TService instance)
        where TService : notnull
    {
        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        _services[typeof(TService)] = instance;
        return this;
    }

    /// <summary>Registers an instance to be returned for the given <paramref name="serviceType"/>.</summary>
    public FakeServiceProvider Add(Type serviceType, object instance)
    {
        if (serviceType is null)
        {
            throw new ArgumentNullException(nameof(serviceType));
        }

        if (instance is null)
        {
            throw new ArgumentNullException(nameof(instance));
        }

        _services[serviceType] = instance;
        return this;
    }

    /// <inheritdoc />
    public object? GetService(Type serviceType) =>
        _services.TryGetValue(serviceType, out var instance) ? instance : null;
}
