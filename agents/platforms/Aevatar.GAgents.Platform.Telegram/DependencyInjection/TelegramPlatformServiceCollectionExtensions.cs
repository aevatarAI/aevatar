using Aevatar.AI.ToolProviders.NyxId;
using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Platform.Telegram;

/// <summary>
/// DI registration entry point for the Telegram platform package.
/// </summary>
public static class TelegramPlatformServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Telegram message composer + native producer + payload redactor.
    /// </summary>
    public static IServiceCollection AddTelegramPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<TelegramMessageComposer>();
        services.TryAddSingleton<TelegramChannelNativeMessageProducer>();
        services.TryAddSingleton<NyxIdToolOptions>();
        services.TryAddSingleton<NyxIdApiClient>();
        services.TryAddSingleton<TelegramChannelNativeMessageSender>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IMessageComposer, TelegramMessageComposer>(
            sp => sp.GetRequiredService<TelegramMessageComposer>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelNativeMessageProducer, TelegramChannelNativeMessageProducer>(
            sp => sp.GetRequiredService<TelegramChannelNativeMessageProducer>()));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IChannelNativeMessageSender, TelegramChannelNativeMessageSender>(
            sp => sp.GetRequiredService<TelegramChannelNativeMessageSender>()));
        services.TryAddSingleton<TelegramPayloadRedactor>();

        return services;
    }
}
