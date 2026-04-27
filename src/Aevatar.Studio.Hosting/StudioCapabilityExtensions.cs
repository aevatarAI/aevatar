using Aevatar.Hosting;
using Aevatar.Studio.Application.Studio.Services;
using Aevatar.Studio.Hosting.Endpoints;
using Microsoft.AspNetCore.Builder;
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
                services.AddStudioHostingCore(config);
                services.AddStudioBridgeServices();
                services.AddStudioAuthoringServices();
                services.AddAevatarHealthContributor(new AevatarHealthContributorRegistration
                {
                    Name = "studio",
                    Category = "capability",
                    RequiredRoutes =
                    [
                        "/api/health",
                        "/api/auth/me",
                        "/api/app/context",
                        "/api/connectors",
                        "/api/editor/validate",
                        "/api/executions",
                        "/api/roles",
                        "/api/settings",
                        "/api/workspace",
                    ],
                    ProbeAsync = static async (serviceProvider, cancellationToken) =>
                    {
                        var settingsService = serviceProvider.GetRequiredService<SettingsService>();
                        _ = await settingsService.GetAsync(cancellationToken);

                        var workspaceService = serviceProvider.GetRequiredService<WorkspaceService>();
                        _ = await workspaceService.GetSettingsAsync(cancellationToken);

                        return AevatarHealthContributorResult.Healthy(
                            "Studio capability is ready.",
                            new Dictionary<string, string>(StringComparer.Ordinal)
                            {
                                ["scopeBoundCatalogApis"] = "/api/connectors, /api/roles",
                            });
                    },
                });
            },
            static app =>
            {
                app.MapControllers();
                StudioEndpoints.Map(app, embeddedWorkflowMode: true);
                StudioMemberEndpoints.Map(app);
                Controllers.ChatHistoryEndpoints.MapChatHistoryEndpoints(app);
                app.MapExplorerEndpoints();
            });
    }
}
