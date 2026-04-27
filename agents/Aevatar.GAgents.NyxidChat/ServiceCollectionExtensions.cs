using System.Runtime.CompilerServices;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Aevatar.GAgents.Channel.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgents.NyxidChat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNyxIdChat(this IServiceCollection services, IConfiguration? configuration = null)
    {
        RuntimeHelpers.RunClassConstructor(typeof(NyxIdChatGAgent).TypeHandle);

        services.AddHttpClient();
        services.TryAddSingleton(provider => BindRelayOptions(configuration));
        services.TryAddSingleton<Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions>(
            provider => provider.GetRequiredService<NyxIdRelayOptions>());
        services.TryAddSingleton<INyxIdRelayReplayGuard>(provider =>
        {
            var options = provider.GetRequiredService<Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions>();
            return new NyxIdRelayReplayGuard(
                TimeSpan.FromSeconds(Math.Max(1, options.CallbackReplayWindowSeconds)),
                TimeProvider.System);
        });
        services.TryAddSingleton<NyxIdRelayTransport>();
        services.TryAddSingleton<NyxIdRelayAuthValidator>();

        // ─── Channel LLM reply inbox runtime + hosted service ───
        services.TryAddSingleton<ChannelLlmReplyInboxRuntime>();
        services.TryAddSingleton<IChannelLlmReplyInbox>(sp => sp.GetRequiredService<ChannelLlmReplyInboxRuntime>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, ChannelLlmReplyInboxHostedService>());

        // ─── Conversation turn-runner override + reply generator ───
        services.Replace(ServiceDescriptor.Singleton<IConversationTurnRunner, ChannelConversationTurnRunner>());
        services.TryAddSingleton<IConversationReplyGenerator, NyxIdConversationReplyGenerator>();

        return services;
    }

    private static NyxIdRelayOptions BindRelayOptions(IConfiguration? configuration)
    {
        var options = new NyxIdRelayOptions();
        configuration?.GetSection("Aevatar:NyxId:Relay").Bind(options);
        return options;
    }
}
