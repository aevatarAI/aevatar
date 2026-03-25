using Aevatar.Studio.Application.Studio.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Application.Studio.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioApplication(this IServiceCollection services)
    {
        services.AddSingleton<WorkflowGraphMapper>();
        services.AddSingleton<TextDiffService>();
        services.AddSingleton<WorkflowEditorService>();
        services.AddSingleton<WorkspaceService>();
        services.AddSingleton<ExecutionService>();
        services.AddSingleton<ConnectorService>();
        services.AddSingleton<RoleCatalogService>();
        services.AddSingleton<SettingsService>();
        return services;
    }
}
