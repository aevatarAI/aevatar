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
    /// Registers the NyxID relay channel: API client, provisioning services (Lark + Telegram),
    /// API-key ownership verifier, scope resolver, channel reply service, outbound port,
    /// replay guard, and interactive reply dispatcher.
    /// </summary>
    public static IServiceCollection AddNyxIdRelayChannel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<NyxIdApiClient>();
        services.TryAddSingleton<INyxLarkProvisioningService, NyxLarkProvisioningService>();
        services.TryAddSingleton<INyxTelegramProvisioningService, NyxTelegramProvisioningService>();
        services.TryAddSingleton<INyxRelayApiKeyOwnershipVerifier, NyxRelayApiKeyOwnershipVerifier>();
        services.TryAddSingleton<INyxIdRelayScopeResolver, NyxIdRelayScopeResolver>();

        // Provisioning service set — both Lark + Telegram are concrete provisioning sources.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxChannelBotProvisioningService, NyxLarkProvisioningService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxChannelBotProvisioningService, NyxTelegramProvisioningService>());

        services.TryAddSingleton<ChannelPlatformReplyService>();
        services.TryAddSingleton<NyxIdRelayOutboundPort>();
        services.TryAddSingleton<INyxIdRelayReplayGuard>(sp =>
        {
            var relayOptions = sp.GetService<NyxIdRelayOptions>() ?? new NyxIdRelayOptions();
            return new NyxIdRelayReplayGuard(
                TimeSpan.FromSeconds(Math.Max(1, relayOptions.CallbackReplayWindowSeconds)),
                TimeProvider.System);
        });
        services.TryAddSingleton<IInteractiveReplyDispatcher, NyxIdRelayInteractiveReplyDispatcher>();

        return services;
    }
}
