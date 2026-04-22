using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.AI.ToolProviders.NyxId;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.Lark;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddLarkTools(
        this IServiceCollection services,
        Action<LarkToolOptions>? configure = null)
    {
        var options = new LarkToolOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<NyxIdToolOptions>();
        services.TryAddSingleton<NyxIdApiClient>();
        services.TryAddSingleton<ILarkNyxClient, LarkNyxClient>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, LarkAgentToolSource>());

        return services;
    }
}
