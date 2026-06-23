using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Channel.NyxIdRelay;

/// <summary>
/// DI registration entry point for the NyxID relay channel package.
/// </summary>
public static class NyxIdRelayChannelServiceCollectionExtensions
{
    /// <summary>
    /// Registers the NyxID relay channel: API client, outbound port, and interactive reply
    /// dispatcher.
    /// </summary>
    // A Lark/Feishu bot is registered directly on NyxID; aevatar no longer self-provisions bots or
    // maintains a local registration mirror. Inbound relay turns derive scope from the validated
    // callback JWT and reply through the relay reply token via NyxIdRelayOutboundPort, so the relay
    // package only registers the NyxID API client, the outbound port, and the interactive reply
    // dispatcher.
    public static IServiceCollection AddNyxIdRelayChannel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<NyxIdApiClient>();
        services.TryAddSingleton<NyxIdRelayOutboundPort>();
        services.TryAddSingleton<IInteractiveReplyDispatcher, NyxIdRelayInteractiveReplyDispatcher>();

        return services;
    }
}
