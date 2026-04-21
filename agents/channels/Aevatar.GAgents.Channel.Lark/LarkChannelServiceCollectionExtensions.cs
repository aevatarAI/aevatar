using Aevatar.GAgents.Channel.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.Channel.Lark;

public static class LarkChannelServiceCollectionExtensions
{
    public static IServiceCollection AddLarkChannel(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<LarkPayloadRedactor>();
        services.TryAddSingleton<LarkMessageComposer>();
        services.TryAddSingleton<LarkChannelAdapter>();
        services.TryAddSingleton<IMessageComposer>(sp => sp.GetRequiredService<LarkMessageComposer>());
        services.TryAddSingleton<IMessageComposer<LarkOutboundMessage>>(sp => sp.GetRequiredService<LarkMessageComposer>());
        services.TryAddSingleton<IChannelTransport>(sp => sp.GetRequiredService<LarkChannelAdapter>());
        services.TryAddSingleton<IChannelOutboundPort>(sp => sp.GetRequiredService<LarkChannelAdapter>());

        return services;
    }
}
