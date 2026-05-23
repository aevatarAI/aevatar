using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.DependencyInjection;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Studio.Hosting.NyxId;
using Aevatar.Studio.Infrastructure.DependencyInjection;
using Aevatar.Studio.Infrastructure.ScopeResolution; // DefaultAppScopeResolver
using Aevatar.Studio.Projection.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aevatar.Studio.Hosting;

internal static class StudioHostingServiceCollectionExtensions
{
    internal static IServiceCollection AddStudioHostingCore(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<StudioHostingOptions>(configuration.GetSection(StudioHostingOptions.SectionName));
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
        services.TryAddSingleton<IUserLlmCatalogPort, NyxIdLlmCatalogHttpClient>();
        services.AddStudioInfrastructure(configuration);
        services.AddStudioProjectionComponents(configuration);
        services.AddStudioProjectionReadModelProviders(configuration);
        return services;
    }

    internal static IServiceCollection AddStudioBridgeServices(this IServiceCollection services)
    {
        services.AddSingleton(sp => new AppScopedWorkflowService(
            sp.GetRequiredService<IWorkflowYamlDocumentService>(),
            sp.GetService<IStudioWorkspaceQueryPort>(),
            sp.GetService<IStudioWorkspaceCommandPort>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AppScopedWorkflowService>>()));
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
        // Refactor (iter21/cluster-001):
        //   Old pattern: Host registered fake authoring actors, process gates, and ChatRuntime factories.
        //   New principle: Host keeps only protocol helpers; Application/Infrastructure own authoring preview behavior.
        services.AddSingleton<ScriptEditorValidationService>();
        return services;
    }
}
