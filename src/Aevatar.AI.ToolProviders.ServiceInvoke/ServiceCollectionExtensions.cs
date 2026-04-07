using Aevatar.AI.Abstractions.ToolProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.AI.ToolProviders.ServiceInvoke;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddServiceInvokeTools(
        this IServiceCollection services,
        Action<ServiceInvokeOptions> configure)
    {
        var options = new ServiceInvokeOptions();
        configure(options);
        services.TryAddSingleton(options);
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IAgentToolSource, ServiceInvokeAgentToolSource>());
        return services;
    }
}
