using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Aevatar.GAgents.Platform.Lark;

/// <summary>
/// DI registration entry point for the Lark platform package: HTTP client, message
/// composer, native message producer, payload redactor, and durable inbox runtime.
/// </summary>
public static class LarkPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Lark platform services: a named <see cref="HttpClient"/> for the
    /// proxied Lark host, the Lark <see cref="IMessageComposer"/> /
    /// <see cref="IChannelNativeMessageProducer"/> pair, the payload redactor, and the
    /// durable inbox runtime + hosted service.
    /// </summary>
    public static IServiceCollection AddLarkPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(LarkConversationHostDefaults.HttpClientName, client =>
        {
            client.BaseAddress = LarkConversationHostDefaults.BaseAddress;
        });
        services.TryAddSingleton<LarkMessageComposer>();
        services.TryAddSingleton<LarkChannelNativeMessageProducer>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageComposer, LarkMessageComposer>(
            sp => sp.GetRequiredService<LarkMessageComposer>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelNativeMessageProducer, LarkChannelNativeMessageProducer>(
            sp => sp.GetRequiredService<LarkChannelNativeMessageProducer>()));
        services.TryAddSingleton<LarkPayloadRedactor>();

        // ─── Lark durable inbox runtime + hosted service ───
        services.TryAddSingleton<LarkConversationInboxRuntime>();
        services.TryAddSingleton<ILarkConversationInbox>(sp => sp.GetRequiredService<LarkConversationInboxRuntime>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, LarkConversationInboxHostedService>());

        return services;
    }
}
