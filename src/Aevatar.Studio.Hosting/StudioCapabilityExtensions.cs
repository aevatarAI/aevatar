using Aevatar.Capabilities;
using Aevatar.Studio.Hosting.Endpoints;
using Microsoft.AspNetCore.Builder;

namespace Aevatar.Studio.Hosting;

public static class StudioCapabilityExtensions
{
    /// <summary>
    /// Registers the Studio capability: editor, connectors, roles, workspace, executions.
    /// All endpoints are mapped under /api/editor, /api/connectors, /api/roles, /api/workspace,
    /// /api/executions, /api/app.
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
                        "/api/workspace",
                    ],
                });
            },
            static app =>
            {
                app.MapControllers();
                StudioEndpoints.Map(app, embeddedWorkflowMode: true);
                WorkflowBoardSnapshotEndpoints.Map(app);
                StudioMemberEndpoints.Map(app);
                StudioMemberAutomationEndpoints.Map(app);
                StudioProvisioningEndpoints.Map(app);
                StudioTeamEndpoints.Map(app);
                ContentArtifactEndpoints.Map(app);
                WorkOrderEndpoints.Map(app);
                NyxIdLoginFinalizationEndpoints.Map(app);
                Controllers.ChatHistoryEndpoints.MapChatHistoryEndpoints(app);
                app.MapExplorerEndpoints();
            });
    }
}
