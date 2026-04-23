using System.Runtime.CompilerServices;
using Aevatar.GAgents.Channel.NyxIdRelay;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        services.TryAddSingleton<NyxIdRelayTransport>();
        services.TryAddSingleton<NyxIdRelayAuthValidator>();

        return services;
    }

    private static NyxIdRelayOptions BindRelayOptions(IConfiguration? configuration)
    {
        var options = new NyxIdRelayOptions();
        configuration?.GetSection("Aevatar:NyxId:Relay").Bind(options);
        return options;
    }
}
