using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// Fluent builder that composes one <see cref="ChannelPipeline"/> from ordered
/// <see cref="IChannelMiddleware"/> registrations.
/// </summary>
/// <remarks>
/// The builder does not own middleware lifecycle; singleton + scoped lookup is delegated to the
/// <see cref="IServiceProvider"/> captured at <see cref="Build"/> time. Registration order is
/// preserved and matches the invocation order inside <see cref="ChannelPipeline.InvokeAsync"/>.
/// </remarks>
public sealed class MiddlewarePipelineBuilder
{
    private readonly List<Func<IServiceProvider, IChannelMiddleware>> _factories = [];

    /// <summary>
    /// Adds a middleware type resolved via DI at build time.
    /// </summary>
    public MiddlewarePipelineBuilder Use<TMiddleware>() where TMiddleware : IChannelMiddleware
    {
        _factories.Add(sp => ActivatorUtilities.GetServiceOrCreateInstance<TMiddleware>(sp));
        return this;
    }

    /// <summary>
    /// Adds a pre-constructed middleware instance.
    /// </summary>
    public MiddlewarePipelineBuilder Use(IChannelMiddleware middleware)
    {
        ArgumentNullException.ThrowIfNull(middleware);
        _factories.Add(_ => middleware);
        return this;
    }

    /// <summary>
    /// Adds a factory-backed middleware registration.
    /// </summary>
    public MiddlewarePipelineBuilder Use(Func<IServiceProvider, IChannelMiddleware> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories.Add(factory);
        return this;
    }

    /// <summary>
    /// Resolves and freezes the middleware list into a pipeline.
    /// </summary>
    public ChannelPipeline Build(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var middlewares = new IChannelMiddleware[_factories.Count];
        for (var i = 0; i < _factories.Count; i++)
            middlewares[i] = _factories[i](services);
        return new ChannelPipeline(middlewares);
    }
}
