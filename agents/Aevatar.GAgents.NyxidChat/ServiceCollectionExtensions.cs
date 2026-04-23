using System.Runtime.CompilerServices;
using Aevatar.GAgents.NyxidChat.Relay;
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
        services.TryAddSingleton<NyxRelayJwtValidator>();
        services.TryAddSingleton<INyxRelayBridgeIdempotencyGuard, NyxRelayBridgeIdempotencyGuard>();

        return services;
    }

    private static NyxIdRelayOptions BindRelayOptions(IConfiguration? configuration)
    {
        var options = new NyxIdRelayOptions();
        configuration?.GetSection("Aevatar:NyxId:Relay").Bind(options);
        return options;
    }
}
