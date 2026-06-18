using Aevatar.AI.Abstractions.ToolProviders;
using Aevatar.Foundation.Abstractions.HumanInteraction;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Workflow.Integration.AI;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSkillBackedHumanInteractionDelivery(
        this IServiceCollection services,
        Action<SkillBackedHumanInteractionPortOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure != null)
            services.Configure(configure);

        services.Replace(ServiceDescriptor.Singleton<IHumanInteractionPort, SkillBackedHumanInteractionPort>());
        return services;
    }

    public static IServiceCollection AddChannelBackedHumanInteractionTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentToolSource, HumanInteractionChannelToolSource>());
        return services;
    }
}
