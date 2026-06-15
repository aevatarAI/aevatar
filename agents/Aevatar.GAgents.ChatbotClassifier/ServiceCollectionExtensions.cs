using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.GAgents.ChatbotClassifier;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddChatbotClassifier(this IServiceCollection services)
    {
        RuntimeHelpers.RunClassConstructor(typeof(ChatbotClassifierGAgent).TypeHandle);
        return services;
    }
}
