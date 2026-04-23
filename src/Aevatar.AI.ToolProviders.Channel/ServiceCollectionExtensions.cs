using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Channel;

/// <summary>DI registration for the channel-neutral interactive reply tools.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the channel-neutral interactive reply tool source together with the shared
    /// <see cref="IInteractiveReplyCollector"/> and <see cref="IChannelMessageComposerRegistry"/>
    /// so LLM turns can stage card replies into the relay finalize path.
    /// </summary>
    public static IServiceCollection AddChannelInteractiveReplyTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IInteractiveReplyCollector, AsyncLocalInteractiveReplyCollector>();
        services.TryAddSingleton<IChannelMessageComposerRegistry, ChannelMessageComposerRegistry>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, ChannelInteractiveReplyToolSource>());

        return services;
    }
}
