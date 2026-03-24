using Aevatar.Hosting;
using Aevatar.Studio.Hosting.Endpoints;
using Microsoft.AspNetCore.Builder;

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
            },
            static app =>
            {
                app.MapControllers();
                StudioEndpoints.Map(app, embeddedWorkflowMode: true);
            });
    }
}
