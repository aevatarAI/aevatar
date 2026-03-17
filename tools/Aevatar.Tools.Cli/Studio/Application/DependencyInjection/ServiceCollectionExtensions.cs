using Aevatar.Tools.Cli.Studio.Application.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Studio.Application.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioApplication(this IServiceCollection services)
    {
        services.AddSingleton<WorkflowGraphMapper>();
        services.AddSingleton<TextDiffService>();
        services.AddSingleton<WorkflowEditorService>();
        services.AddSingleton<BundleService>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<ExecutionService>();
        services.AddSingleton<ConnectorService>();
        services.AddSingleton<RoleCatalogService>();
        services.AddSingleton<SettingsService>();
        return services;
    }
}
