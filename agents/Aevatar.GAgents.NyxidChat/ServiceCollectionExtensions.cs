using System.Runtime.CompilerServices;
using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.GAgents.NyxidChat;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNyxIdChat(
        this IServiceCollection services,
        Action<NyxIdRelayOptions>? configureRelay = null)
    {
        RuntimeHelpers.RunClassConstructor(typeof(NyxIdChatGAgent).TypeHandle);

        services.TryAddSingleton<NyxIdChatActorStore>();
        services.TryAddSingleton<NyxIdRelayPairingStore>();
        services.TryAddSingleton(new NyxIdRelayOptions());

        // Register pairing tool so NyxIdChat agent can approve pairing codes
        services.AddSingleton<IAgentToolSource, NyxIdPairingToolSource>();

        return services;
    }
}
