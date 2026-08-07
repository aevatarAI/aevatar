using Aevatar.BackendConsole.Hosting;
using Aevatar.Workflow.Infrastructure.CapabilityApi;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Mainnet.Host.Api.BackendConsole;

internal static class AdminConsoleEndpoints
{
    private const string PageRoute = "/admin";
    private const string StudioPageRoute = "/admin/studio";

    private static readonly BackendConsoleAsset PageAsset = new(
        LogicalName: "admin",
        Assembly: typeof(AdminConsoleEndpoints).Assembly,
        ResourceSuffix: "BackendConsole.admin.html",
        ContentType: "text/html",
        InjectHostConfiguration: true);

    private static readonly BackendConsoleAsset StudioPageAsset = new(
        LogicalName: "admin-studio",
        Assembly: typeof(WorkflowStudioEndpoints).Assembly,
        ResourceSuffix: "CapabilityApi.studio-assistant.html",
        ContentType: "text/html",
        InjectHostConfiguration: true);

    public static IEndpointRouteBuilder MapAdminConsoleEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetAdminConsolePage)
            .WithTags("BackendConsole")
            .WithName("GetAdminConsolePage")
            .WithSummary("Fused backend admin console served from an embedded static asset.")
            .AllowAnonymous();

        app.MapGet(StudioPageRoute, GetAdminStudioPage)
            .WithTags("BackendConsole")
            .WithName("GetAdminStudioPage")
            .WithSummary("Admin-owned Studio surface served from the canonical embedded Studio asset.")
            .AllowAnonymous();

        return app;
    }

    internal static IResult GetAdminConsolePage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(PageAsset);
    }

    internal static IResult GetAdminStudioPage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(StudioPageAsset);
    }
}
