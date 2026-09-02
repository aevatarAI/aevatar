using Aevatar.BackendConsole.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

// Workflow Studio: conversational orchestration surface. Mirrors the observatory endpoint shape — the page
// (and its OIDC PKCE callback target) is served anonymously as a self-contained static shell; the in-page JS
// gates the app behind a nyxid bearer login exactly like observatory. This increment is mount + login only:
// no data endpoints are mapped here. The live data wiring reuses existing APIs (/api/chat,
// /api/studio/context, /api/schedules) and is added in a later increment.
public static class WorkflowStudioEndpoints
{
    private const string PageRoute = "/workflow/studio";
    private const string SchedulesRoute = "/schedules";
    private const string CallbackRoute = "/workflow/studio/callback";

    private static readonly BackendConsoleAsset PageAsset = new(
        LogicalName: "workflow-studio",
        Assembly: typeof(WorkflowStudioEndpoints).Assembly,
        ResourceSuffix: "CapabilityApi.workflow-studio.html",
        ContentType: "text/html",
        InjectHostConfiguration: true);

    public static IEndpointRouteBuilder MapWorkflowStudio(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetStudioPage)
            .WithTags("WorkflowStudio")
            .WithName("GetWorkflowStudioPage")
            .WithSummary("Conversational workflow studio served from an embedded static asset.")
            .AllowAnonymous();

        // Standalone schedules view: same self-contained shell + login gate as the studio
        // page; the in-page JS renders a full-screen schedules list when path is /schedules.
        // Reuses the studio OIDC callback (/workflow/studio/callback) + storageKey, so no new
        // NyxID redirect_uri registration is required.
        app.MapGet(SchedulesRoute, GetStudioPage)
            .WithTags("WorkflowStudio")
            .WithName("GetWorkflowSchedulesPage")
            .WithSummary("Standalone schedules view (studio shell, full-screen schedules).")
            .AllowAnonymous();

        app.MapGet(CallbackRoute, GetStudioPage)
            .WithTags("WorkflowStudio")
            .WithName("GetWorkflowStudioCallback")
            .WithSummary("OIDC PKCE redirect target consumed by the studio page JS.")
            .AllowAnonymous();

        return app;
    }

    internal static IResult GetStudioPage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(PageAsset);
    }
}
