using Aevatar.AIWorkspace.Application;
using Aevatar.AIWorkspace.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Mainnet.Host.Api.AI;

public sealed class AIWorkspaceOptions
{
    public const string SectionName = "Aevatar:AIWorkspace";

    public string StaticAssetsPath { get; set; } = "AIWorkspaceWeb";
}

internal static class AIWorkspaceServiceCollectionExtensions
{
    public static IServiceCollection AddAIWorkspace(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AIWorkspaceOptions>()
            .Bind(configuration.GetSection(AIWorkspaceOptions.SectionName))
            .Validate(
                static options => !string.IsNullOrWhiteSpace(options.StaticAssetsPath),
                $"{AIWorkspaceOptions.SectionName}:{nameof(AIWorkspaceOptions.StaticAssetsPath)} " +
                "must name the directory containing the built AI workspace assets.")
            .ValidateOnStart();
        services.TryAddSingleton<IAIWorkspaceWebAssetService, AIWorkspaceWebAssetService>();
        services.TryAddSingleton<IAIWorkspaceAgentsQueryService, AIWorkspaceAgentsQueryService>();
        services.TryAddSingleton<IAIWorkspaceModelsQueryService, AIWorkspaceModelsQueryService>();
        services.TryAddSingleton<IAIWorkspaceActivityQueryService, AIWorkspaceActivityQueryService>();
        services.TryAddSingleton<IAIWorkspaceOverviewQueryService, AIWorkspaceOverviewQueryService>();
        return services;
    }
}
