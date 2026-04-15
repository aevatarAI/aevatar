using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.DependencyInjection;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Studio.Infrastructure.DependencyInjection;
using Aevatar.Studio.Infrastructure.ScopeResolution; // DefaultAppScopeResolver
using Aevatar.Studio.Projection.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Hosting;

internal static class StudioHostingServiceCollectionExtensions
{
    internal static IServiceCollection AddStudioHostingCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddControllers()
            .AddApplicationPart(typeof(EditorController).Assembly)
            .AddJsonOptions(json =>
            {
                json.JsonSerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
                json.JsonSerializerOptions.DefaultIgnoreCondition =
                    System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
            });
        services.AddHttpContextAccessor();
        services.AddSingleton<IAppScopeResolver, DefaultAppScopeResolver>();
        services.AddStudioApplication();
        services.AddStudioInfrastructure(configuration);
        services.AddStudioProjectionComponents();
        return services;
    }

    internal static IServiceCollection AddStudioBridgeServices(this IServiceCollection services)
    {
        services.AddSingleton(sp => new AppScopedWorkflowService(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IWorkflowYamlDocumentService>(),
            sp.GetService<IScopeWorkflowQueryPort>(),
            sp.GetService<IScopeWorkflowCommandPort>(),
            sp.GetService<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowActorBindingReader>(),
            sp.GetService<Aevatar.GAgentService.Abstractions.Ports.IServiceRevisionArtifactStore>(),
            sp.GetService<Aevatar.GAgentService.Abstractions.Ports.IServiceLifecycleQueryPort>(),
            sp.GetService<IWorkflowStoragePort>()));
        services.AddSingleton(sp => new AppScopedScriptService(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetService<IScopeScriptQueryPort>(),
            sp.GetService<IScopeScriptCommandPort>(),
            sp.GetService<IScopeScriptSaveObservationPort>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptDefinitionSnapshotPort>(),
            sp.GetService<Aevatar.Scripting.Application.IScriptEvolutionApplicationService>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptCatalogQueryPort>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptEvolutionDecisionReadPort>(),
            sp.GetService<Aevatar.Scripting.Core.Ports.IScriptingActorAddressResolver>(),
            sp.GetService<Aevatar.Scripting.Application.Queries.IScriptReadModelQueryApplicationService>(),
            sp.GetService<IScriptStoragePort>()));
        return services;
    }

    internal static IServiceCollection AddStudioAuthoringServices(this IServiceCollection services)
    {
        services.AddSingleton<AppAuthoringChatSessionFactory>();
        services.AddSingleton<WorkflowGeneratePromptCatalog>();
        services.AddSingleton<WorkflowGenerateOrchestrator>();
        services.AddSingleton<WorkflowGenerateActorService>();
        services.AddSingleton<ScriptGeneratePromptCatalog>();
        services.AddSingleton<ScriptGenerateOrchestrator>();
        services.AddSingleton<ScriptGenerateActorService>();
        services.AddSingleton<ScriptEditorValidationService>();
        return services;
    }
}
