using Aevatar.Tools.Cli.Studio.Application.Abstractions;
using Aevatar.Tools.Cli.Studio.Domain.Compatibility;
using Aevatar.Tools.Cli.Studio.Domain.Services;
using Aevatar.Tools.Cli.Studio.Infrastructure.Serialization;
using Aevatar.Tools.Cli.Studio.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Tools.Cli.Studio.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<StudioStorageOptions>(configuration.GetSection("Studio:Storage"));
        services.AddSingleton(WorkflowCompatibilityProfile.AevatarV1);
        services.AddSingleton<WorkflowDocumentNormalizer>();
        services.AddSingleton<WorkflowValidator>();
        services.AddSingleton<IWorkflowYamlDocumentService, YamlWorkflowDocumentService>();
        services.AddSingleton<IStudioWorkspaceStore, FileStudioWorkspaceStore>();
        services.AddSingleton<IAevatarSettingsStore, FileAevatarSettingsStore>();
        return services;
    }
}
