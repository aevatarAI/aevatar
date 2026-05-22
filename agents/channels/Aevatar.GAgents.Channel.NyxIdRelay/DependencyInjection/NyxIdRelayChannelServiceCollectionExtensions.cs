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
    /// scope resolver, channel reply service, outbound port, and interactive reply dispatcher.
    /// </summary>
    // Refactor (iter17/cluster-038):
    //   Old pattern: Nyx relay replay/idempotency 和 reply 累积在 process-local ConcurrentDictionary/lock(NyxRelayBridgeIdempotencyGuard / NyxIdRelayReplayGuard / NyxIdRelayReplyAccumulator)。
    //   New principle: ConversationGAgent persist callback_jti admission 为 typed event 优先于 business work;删除 process-local replay guards + dead accumulator。
    public static IServiceCollection AddNyxIdRelayChannel(this IServiceCollection services)
    {
        // Refactor (iter27/cluster-003-channel-registration-scope-backfill):
        //   Old pattern: repair_lark_mirror registered a live Nyx API-key ownership verifier for local mirror repair.
        //   New principle: no live repair surface remains; recovery uses register_lark_via_nyx plus projection-only rebuild.
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<NyxIdApiClient>();
        services.TryAddSingleton<INyxLarkProvisioningService, NyxLarkProvisioningService>();
        services.TryAddSingleton<INyxTelegramProvisioningService, NyxTelegramProvisioningService>();
        services.TryAddSingleton<INyxIdRelayScopeResolver, NyxIdRelayScopeResolver>();

        // Provisioning service set — both Lark + Telegram are concrete provisioning sources.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxChannelBotProvisioningService, NyxLarkProvisioningService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxChannelBotProvisioningService, NyxTelegramProvisioningService>());

        services.TryAddSingleton<ChannelPlatformReplyService>();
        services.TryAddSingleton<NyxIdRelayOutboundPort>();
        services.TryAddSingleton<IInteractiveReplyDispatcher, NyxIdRelayInteractiveReplyDispatcher>();

        return services;
    }
}
