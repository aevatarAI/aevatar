using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Channel.Lark;

public static class LarkChannelServiceCollectionExtensions
{
    public static IServiceCollection AddLarkChannel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(LarkChannelDefaults.HttpClientName, client =>
        {
            client.BaseAddress = LarkChannelDefaults.DefaultBaseAddress;
        });
        services.TryAddSingleton<LarkPayloadRedactor>();
        services.TryAddSingleton<LarkMessageComposer>();
        services.TryAddSingleton<LarkChannelAdapter>(sp => new LarkChannelAdapter(
            sp.GetRequiredService<Aevatar.Foundation.Abstractions.Credentials.ICredentialProvider>(),
            sp.GetRequiredService<LarkMessageComposer>(),
            sp.GetRequiredService<LarkPayloadRedactor>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LarkChannelAdapter>>(),
            sp.GetRequiredService<System.Net.Http.IHttpClientFactory>().CreateClient(LarkChannelDefaults.HttpClientName)));
        services.TryAddSingleton<IMessageComposer>(sp => sp.GetRequiredService<LarkMessageComposer>());
        services.TryAddSingleton<IMessageComposer<LarkOutboundMessage>>(sp => sp.GetRequiredService<LarkMessageComposer>());
        services.TryAddSingleton<IChannelTransport>(sp => sp.GetRequiredService<LarkChannelAdapter>());
        services.TryAddSingleton<IChannelOutboundPort>(sp => sp.GetRequiredService<LarkChannelAdapter>());

        return services;
    }
}
