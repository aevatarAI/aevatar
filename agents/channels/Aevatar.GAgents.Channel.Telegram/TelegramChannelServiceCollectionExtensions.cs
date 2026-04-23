using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Channel.Telegram;

public static class TelegramChannelServiceCollectionExtensions
{
    public static IServiceCollection AddTelegramChannel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(TelegramChannelDefaults.HttpClientName, client =>
        {
            client.BaseAddress = TelegramChannelDefaults.DefaultBaseAddress;
        });
        services.TryAddSingleton<TelegramChannelAdapterOptions>();
        services.TryAddSingleton<TelegramPayloadRedactor>();
        services.TryAddSingleton<TelegramMessageComposer>();
        services.TryAddSingleton<TelegramChannelAdapter>(sp => new TelegramChannelAdapter(
            sp.GetRequiredService<Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider>(),
            sp.GetRequiredService<TelegramMessageComposer>(),
            sp.GetRequiredService<TelegramPayloadRedactor>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<TelegramChannelAdapter>>(),
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(TelegramChannelDefaults.HttpClientName),
            sp.GetService<ITelegramAttachmentContentResolver>(),
            sp.GetRequiredService<TelegramChannelAdapterOptions>()));
        services.TryAddSingleton<IMessageComposer>(sp => sp.GetRequiredService<TelegramMessageComposer>());
        services.TryAddSingleton<IMessageComposer<TelegramOutboundMessage>>(sp => sp.GetRequiredService<TelegramMessageComposer>());
        services.TryAddSingleton<IPayloadRedactor>(sp => sp.GetRequiredService<TelegramPayloadRedactor>());
        services.TryAddSingleton<IChannelTransport>(sp => sp.GetRequiredService<TelegramChannelAdapter>());
        services.TryAddSingleton<IChannelOutboundPort>(sp => sp.GetRequiredService<TelegramChannelAdapter>());

        return services;
    }
}
