using System.Runtime.CompilerServices;
using Aevatar.Foundation.Core.TypeSystem;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChatbotClassifier;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatbotClassifier(this IServiceCollection services)
    {
        RuntimeHelpers.RunClassConstructor(typeof(ChatbotClassifierGAgent).TypeHandle);
        services.AddAevatarAgentKindRegistry(builder => builder.ScanAssemblies(typeof(ChatbotClassifierGAgent).Assembly));
        return services;
    }
}
