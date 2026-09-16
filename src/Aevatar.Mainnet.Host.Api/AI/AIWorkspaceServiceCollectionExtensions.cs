using Aevatar.AIWorkspace.Application;
using Aevatar.AIWorkspace.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Mainnet.Host.Api.AI;

internal static class AIWorkspaceServiceCollectionExtensions
{
    public static IServiceCollection AddAIWorkspace(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.TryAddSingleton<IAIWorkspaceAgentsQueryService, AIWorkspaceAgentsQueryService>();
        services.TryAddSingleton<IAIWorkspaceModelsQueryService, AIWorkspaceModelsQueryService>();
        services.TryAddSingleton<IAIWorkspaceActivityQueryService, AIWorkspaceActivityQueryService>();
        services.TryAddSingleton<IAIWorkspaceOverviewQueryService, AIWorkspaceOverviewQueryService>();
        return services;
    }
}
