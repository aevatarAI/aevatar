using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Channel.Runtime;

/// <summary>
/// DI registration entry point for the channel runtime package.
/// </summary>
public static class ChannelRuntimeServiceCollectionExtensions
{
    /// <summary>
    /// Registers the channel runtime middlewares, diagnostics, and default turn-runner fallback.
    /// Consumers override <see cref="IConversationTurnRunner"/> with a real implementation bound to
    /// their bot + outbound adapter.
    /// </summary>
    public static IServiceCollection AddChannelRuntime(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<ConversationResolverMiddleware>();
        services.TryAddSingleton<LoggingMiddleware>();
        services.TryAddSingleton<TracingMiddleware>();
        services.TryAddSingleton<IConversationTurnRunner, NullConversationTurnRunner>();
        services.TryAddSingleton(_ => new MiddlewarePipelineBuilder()
            .Use<TracingMiddleware>()
            .Use<LoggingMiddleware>()
            .Use<ConversationResolverMiddleware>());

        return services;
    }
}
