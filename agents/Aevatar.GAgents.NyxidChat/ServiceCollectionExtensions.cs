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
        services.TryAddSingleton(BindRelayOptions(configuration));
        services.TryAddSingleton<NyxIdRelayTransport>();
        services.TryAddSingleton<NyxIdRelayAuthValidator>();

        return services;
    }

    private static Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions BindRelayOptions(IConfiguration? configuration)
    {
        var options = new Aevatar.GAgents.Channel.NyxIdRelay.NyxIdRelayOptions();
        configuration?.GetSection("Aevatar:NyxId:Relay").Bind(options);
        return options;
    }
}
