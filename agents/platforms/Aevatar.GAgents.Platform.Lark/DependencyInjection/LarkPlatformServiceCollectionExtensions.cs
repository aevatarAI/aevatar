using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Aevatar.GAgents.Channel.Runtime;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// DI registration entry point for the Lark platform package: HTTP client, message
/// composer, native message producer, and payload redactor.
/// </summary>
public static class LarkPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Lark platform services: a named <see cref="HttpClient"/> for the
    /// proxied Lark host, the Lark <see cref="IMessageComposer"/> /
    /// <see cref="IChannelNativeMessageProducer"/> pair, and the payload redactor.
    /// </summary>
    public static IServiceCollection AddLarkPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Refactor (iter20/cluster-003):
        //   Old pattern: Lark-local durable inbox subscriber worker stream path(orphan)
        //   New principle: delete orphan path,NyxID relay 唯一 ingress
        //
        // AddLarkPlatform stays as an outbound/rendering composition hook. It must not
        // start a Lark-local inbound worker or own conversation ingress state.
        services.AddHttpClient(LarkConversationHostDefaults.HttpClientName, client =>
        {
            client.BaseAddress = LarkConversationHostDefaults.BaseAddress;
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxIdRelayContentAdapter, LarkRelayMessageAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxIdRelayContentAdapter, FeishuRelayMessageAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxIdRelayConversationAdapter, FeishuRelayMessageAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxIdRelayMentionAdapter, FeishuRelayMessageAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxIdRelayInteractionAdapter, FeishuRelayMessageAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxIdRelayConversationAdapter, LarkRelayMessageAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxIdRelayMentionAdapter, LarkRelayMessageAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<INyxIdRelayInteractionAdapter, LarkRelayMessageAdapter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelGroupAdmissionPolicy, LarkGroupAdmissionPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelGroupAdmissionPolicy, FeishuGroupAdmissionPolicy>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelTypingIndicator, LarkTypingIndicator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelTypingIndicator, FeishuTypingIndicator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelReplyTextFormatter, LarkReplyTextFormatter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelReplyTextFormatter, FeishuReplyTextFormatter>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelSubjectContactResolver, LarkSubjectContactResolver>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelSubjectContactResolver, FeishuSubjectContactResolver>());
        services.TryAddSingleton<ILarkBotIdentityResolver, LarkBotIdentityResolver>();
        services.TryAddSingleton<LarkMessageComposer>();
        services.TryAddSingleton<LarkChannelNativeMessageProducer>();
        services.TryAddSingleton<FeishuLarkFamilyMessageComposer>();
        services.TryAddSingleton<FeishuLarkFamilyNativeMessageProducer>();
        services.TryAddSingleton<NyxIdToolOptions>();
        services.TryAddSingleton<NyxIdApiClient>();
        services.TryAddSingleton<ILarkOutboundDispatcher, LarkOutboundDispatcher>();
        services.TryAddSingleton<LarkChannelNativeDeliveryTargetAdapter>();
        services.TryAddSingleton<LarkChannelNativeMessageSender>();
        services.TryAddSingleton<LarkChannelRelayTailTextSender>();
        services.TryAddSingleton<LarkRelayProxyResponseClassifier>();
        services.Replace(ServiceDescriptor.Singleton<IChannelRelayTailTextSender>(
            sp => sp.GetRequiredService<LarkChannelRelayTailTextSender>()));
        services.Replace(ServiceDescriptor.Singleton<IChannelRelayProxyResponseClassifier>(
            sp => sp.GetRequiredService<LarkRelayProxyResponseClassifier>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageComposer, LarkMessageComposer>(
            sp => sp.GetRequiredService<LarkMessageComposer>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageComposer, FeishuLarkFamilyMessageComposer>(
            sp => sp.GetRequiredService<FeishuLarkFamilyMessageComposer>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelNativeMessageProducer, LarkChannelNativeMessageProducer>(
            sp => sp.GetRequiredService<LarkChannelNativeMessageProducer>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelNativeMessageProducer, FeishuLarkFamilyNativeMessageProducer>(
            sp => sp.GetRequiredService<FeishuLarkFamilyNativeMessageProducer>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelNativeMessageSender, LarkChannelNativeMessageSender>(
            sp => sp.GetRequiredService<LarkChannelNativeMessageSender>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelNativeDeliveryTargetAdapter, LarkChannelNativeDeliveryTargetAdapter>(
            sp => sp.GetRequiredService<LarkChannelNativeDeliveryTargetAdapter>()));
        services.TryAddSingleton<LarkPayloadRedactor>();
        services.TryAddSingleton<ILarkOutboundDispatcher, LarkOutboundDispatcher>();

        return services;
    }
}
