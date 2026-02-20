using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Extensions.Maker;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWorkflowMakerExtensions(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IEventModuleFactory, MakerModuleFactory>());
        return services;
    }
}
