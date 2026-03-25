using Aevatar.GAgentService.Abstractions.Ports;
using Aevatar.Hosting;
using Aevatar.Studio.Application;
using Aevatar.Studio.Application.Studio.Abstractions;
using Aevatar.Studio.Application.Studio.DependencyInjection;
using Aevatar.Studio.Hosting.Controllers;
using Aevatar.Studio.Hosting.Endpoints;
using Aevatar.Studio.Infrastructure.DependencyInjection;
using Aevatar.Studio.Infrastructure.ScopeResolution;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aevatar.Studio.Hosting;

public static class StudioCapabilityExtensions
{
    /// <summary>
    /// Registers the Studio capability: editor, connectors, roles, workspace, settings, executions.
    /// All endpoints are mapped under /api/editor, /api/connectors, /api/roles, /api/workspace,
    /// /api/settings, /api/executions, /api/app.
    /// </summary>
    public static WebApplicationBuilder AddStudioCapability(this WebApplicationBuilder builder)
    {
        return builder.AddAevatarCapability(
            "studio",
            static (services, config) =>
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
                services.AddStudioInfrastructure(config);

                // Scoped services bridging Studio UI to GAgentService ports
                services.AddSingleton(sp => new AppScopedWorkflowService(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetRequiredService<IWorkflowYamlDocumentService>(),
                    sp.GetService<IScopeWorkflowQueryPort>(),
                    sp.GetService<IScopeWorkflowCommandPort>(),
                    sp.GetService<Aevatar.Workflow.Application.Abstractions.Runs.IWorkflowActorBindingReader>()));
                services.AddSingleton(sp => new AppScopedScriptService(
                    sp.GetRequiredService<IHttpClientFactory>(),
                    sp.GetService<IScopeScriptQueryPort>(),
                    sp.GetService<IScopeScriptCommandPort>(),
                    sp.GetService<Aevatar.Scripting.Core.Ports.IScriptDefinitionSnapshotPort>(),
                    sp.GetService<Aevatar.Scripting.Application.IScriptEvolutionApplicationService>(),
                    sp.GetService<Aevatar.Scripting.Core.Ports.IScriptCatalogQueryPort>(),
                    sp.GetService<Aevatar.Scripting.Core.Ports.IScriptEvolutionDecisionReadPort>(),
                    sp.GetService<Aevatar.Scripting.Core.Ports.IScriptingActorAddressResolver>(),
                    sp.GetService<Aevatar.Scripting.Application.Queries.IScriptReadModelQueryApplicationService>()));

                // Generation services
                services.AddSingleton<AppAuthoringChatSessionFactory>();
                services.AddSingleton<WorkflowGeneratePromptCatalog>();
                services.AddSingleton<WorkflowGenerateOrchestrator>();
                services.AddSingleton<WorkflowGenerateActorService>();
                services.AddSingleton<ScriptGeneratePromptCatalog>();
                services.AddSingleton<ScriptGenerateOrchestrator>();
                services.AddSingleton<ScriptGenerateActorService>();
                services.AddSingleton<ScriptEditorValidationService>();
            },
            static app =>
            {
                app.MapControllers();
                StudioEndpoints.Map(app, embeddedWorkflowMode: true);
            });
    }
}
