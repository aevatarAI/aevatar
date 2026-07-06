using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.CQRS.Core.Abstractions.Commands;
using Aevatar.CQRS.Core.Commands;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.NyxIdRelay.Outbound;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Scheduled;
using Aevatar.GAgents.Platform.Lark;
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
    // Refactor (iter36/cluster-041-nyx-relay-command-skeleton):
    //   Old pattern: Nyx relay registration endpoints + singleton provisioning services 在 Host 内做 platform selection / scope resolution / remote Nyx provisioning / actor creation / envelope construction / dispatch through raw runtime/dispatch helpers。
    //   New principle: Channel registration 暴露 typed application command facade(reuse existing CQRS command dispatch skeleton);Host 仅 adapt HTTP;provisioning adapters 只调 existing NyxID REST surfaces(**不修改 NyxID 仓库**);local mirror writes 进 standard command skeleton via narrow dispatch port。**不引入新 actor type / 新 envelope / 新 projection phase**(reflector force-pick minimal,排除 structural 的 ChannelRelayRegistrationRunGAgent)。
    public static IServiceCollection AddNyxIdRelayChannel(this IServiceCollection services)
    {
        // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=public rebuild surfaces, new=internal Runtime startup helper only
        // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=relay-local rebuild command pipeline, new=no relay DI for rebuild
        // Refactor (iter56/cluster-933-channel-registration-rebuild-narrow): old=public/manual projection refresh dispatch, new=startup-owned projection refresh
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<NyxIdApiClient>();
        services.TryAddSingleton<ICommandContextPolicy, DefaultCommandContextPolicy>();
        services.TryAddSingleton<ChannelRegistrationCommandFacade>();
        services.TryAddSingleton<ChannelRelayRegistrationFacade>();
        services.TryAddSingleton<ChannelBotRegistrationCommandEnvelopeFactory>();
        services.TryAddSingleton<ChannelRegistrationCommandReceiptFactory>();
        services.TryAddSingleton<ICommandTargetDispatcher<ChannelBotRegistrationCommandTarget>, ActorCommandTargetDispatcher<ChannelBotRegistrationCommandTarget>>();
        services.TryAddSingleton<ICommandTargetResolver<ChannelBotRegisterCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandStartError>, ChannelBotRegistrationCommandTargetResolver<ChannelBotRegisterCommand>>();
        services.TryAddSingleton<ICommandTargetResolver<ChannelBotUnregisterCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandStartError>, ChannelBotRegistrationCommandTargetResolver<ChannelBotUnregisterCommand>>();
        services.TryAddSingleton<ICommandEnvelopeFactory<ChannelBotRegisterCommand>>(sp => sp.GetRequiredService<ChannelBotRegistrationCommandEnvelopeFactory>());
        services.TryAddSingleton<ICommandEnvelopeFactory<ChannelBotUnregisterCommand>>(sp => sp.GetRequiredService<ChannelBotRegistrationCommandEnvelopeFactory>());
        services.TryAddSingleton<ICommandReceiptFactory<ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt>>(sp => sp.GetRequiredService<ChannelRegistrationCommandReceiptFactory>());
        services.TryAddSingleton<ICommandDispatchPipeline<ChannelBotRegisterCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>, DefaultCommandDispatchPipeline<ChannelBotRegisterCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchPipeline<ChannelBotUnregisterCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>, DefaultCommandDispatchPipeline<ChannelBotUnregisterCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchService<ChannelBotRegisterCommand, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>, DefaultCommandDispatchService<ChannelBotRegisterCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>>();
        services.TryAddSingleton<ICommandDispatchService<ChannelBotUnregisterCommand, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>, DefaultCommandDispatchService<ChannelBotUnregisterCommand, ChannelBotRegistrationCommandTarget, ChannelRegistrationCommandAcceptedReceipt, ChannelRegistrationCommandStartError>>();
        services.TryAddSingleton<INyxLarkProvisioningService, NyxLarkProvisioningService>();
        services.TryAddSingleton<INyxTelegramProvisioningService, NyxTelegramProvisioningService>();
        services.TryAddSingleton<INyxChannelBotDeprovisioningService, NyxChannelBotDeprovisioningService>();
        services.TryAddSingleton<INyxIdRelayScopeResolver, NyxIdRelayScopeResolver>();
        services.TryAddSingleton<IChannelRelayActivityRecorder, ChannelRelayActivityRecorder>();

        // Provisioning service set — both Lark + Telegram are concrete provisioning sources.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxChannelBotProvisioningService, NyxLarkProvisioningService>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxChannelBotProvisioningService, NyxTelegramProvisioningService>());

        services.TryAddSingleton<ChannelPlatformReplyService>();
        services.TryAddSingleton<NyxIdRelayOutboundPort>();
        services.TryAddSingleton<LarkOutboundDispatcher>();
        services.TryAddSingleton<ILarkOutboundDispatcher>(sp => sp.GetRequiredService<LarkOutboundDispatcher>());
        services.Replace(ServiceDescriptor.Singleton<IChannelInteractionNotificationPort, NyxIdRelayChannelInteractionNotificationPort>());
        services.TryAddSingleton<IInteractiveReplyDispatcher, NyxIdRelayInteractiveReplyDispatcher>();

        return services;
    }
}
