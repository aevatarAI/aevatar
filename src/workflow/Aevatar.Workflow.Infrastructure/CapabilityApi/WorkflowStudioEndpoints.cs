using Aevatar.BackendConsole.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

// Workflow Studio: the NyxID Assistant shell served as embedded assets. Its browser adapter reuses the
// backend console OIDC token and the canonical /api/chat + conversation endpoints; this endpoint class only
// owns the static page and module routes.
public static class WorkflowStudioEndpoints
{
    private const string PageRoute = "/workflow/studio";
    private const string AssetsRoute = "/workflow/studio/assets";
    private const string SchedulesRoute = "/schedules";
    private const string CallbackRoute = "/workflow/studio/callback";

    private static readonly BackendConsoleAsset AssistantPageAsset = new(
        LogicalName: "studio-assistant",
        Assembly: typeof(WorkflowStudioEndpoints).Assembly,
        ResourceSuffix: "CapabilityApi.studio-assistant.html",
        ContentType: "text/html",
        InjectHostConfiguration: true);

    private static readonly BackendConsoleAsset SchedulesPageAsset = new(
        LogicalName: "workflow-schedules",
        Assembly: typeof(WorkflowStudioEndpoints).Assembly,
        ResourceSuffix: "CapabilityApi.workflow-studio.html",
        ContentType: "text/html",
        InjectHostConfiguration: true);

    private static readonly BackendConsoleAsset AssistantAppAsset = AssistantAsset("app.js", "text/javascript");
    private static readonly BackendConsoleAsset AssistantProtocolAsset = AssistantAsset("protocol.js", "text/javascript");
    private static readonly BackendConsoleAsset AssistantReadinessAsset = AssistantAsset("readiness.js", "text/javascript");
    private static readonly BackendConsoleAsset AssistantActorStateAsset = AssistantAsset("actor-state.js", "text/javascript");
    private static readonly BackendConsoleAsset AssistantBlocksAsset = AssistantAsset("blocks.js", "text/javascript");
    private static readonly BackendConsoleAsset AssistantTransportAsset = AssistantAsset("transport.js", "text/javascript");
    private static readonly BackendConsoleAsset AssistantStylesAsset = AssistantAsset("styles.css", "text/css");

    public static IEndpointRouteBuilder MapWorkflowStudio(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetStudioPage)
            .WithTags("WorkflowStudio")
            .WithName("GetWorkflowStudioPage")
            .WithSummary("Conversational workflow studio served from an embedded static asset.")
            .AllowAnonymous();

        app.MapGet($"{AssetsRoute}/app.js", GetAssistantApp)
            .WithTags("WorkflowStudio")
            .AllowAnonymous();
        app.MapGet($"{AssetsRoute}/protocol.js", GetAssistantProtocol)
            .WithTags("WorkflowStudio")
            .AllowAnonymous();
        app.MapGet($"{AssetsRoute}/readiness.js", GetAssistantReadiness)
            .WithTags("WorkflowStudio")
            .AllowAnonymous();
        app.MapGet($"{AssetsRoute}/actor-state.js", GetAssistantActorState)
            .WithTags("WorkflowStudio")
            .AllowAnonymous();
        app.MapGet($"{AssetsRoute}/blocks.js", GetAssistantBlocks)
            .WithTags("WorkflowStudio")
            .AllowAnonymous();
        app.MapGet($"{AssetsRoute}/transport.js", GetAssistantTransport)
            .WithTags("WorkflowStudio")
            .AllowAnonymous();
        app.MapGet($"{AssetsRoute}/styles.css", GetAssistantStyles)
            .WithTags("WorkflowStudio")
            .AllowAnonymous();

        // Keep the existing schedules shell independent from the Assistant page. Both shells use the
        // shared /auto/callback flow and storage key, so no page-specific NyxID redirect URI is required.
        app.MapGet(SchedulesRoute, GetSchedulesPage)
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

    private static BackendConsoleAsset AssistantAsset(string fileName, string contentType) => new(
        LogicalName: $"studio-assistant-{fileName}",
        Assembly: typeof(WorkflowStudioEndpoints).Assembly,
        ResourceSuffix: $"CapabilityApi.StudioAssistant.{fileName}",
        ContentType: contentType,
        InjectHostConfiguration: false);

    internal static IResult GetStudioPage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(AssistantPageAsset);
    }

    internal static IResult GetSchedulesPage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(SchedulesPageAsset);
    }

    internal static IResult GetAssistantApp(HttpContext http, [FromServices] IBackendConsoleAssetService assets) =>
        ServeAsset(http, assets, AssistantAppAsset);

    internal static IResult GetAssistantProtocol(HttpContext http, [FromServices] IBackendConsoleAssetService assets) =>
        ServeAsset(http, assets, AssistantProtocolAsset);

    internal static IResult GetAssistantReadiness(HttpContext http, [FromServices] IBackendConsoleAssetService assets) =>
        ServeAsset(http, assets, AssistantReadinessAsset);

    internal static IResult GetAssistantActorState(HttpContext http, [FromServices] IBackendConsoleAssetService assets) =>
        ServeAsset(http, assets, AssistantActorStateAsset);

    internal static IResult GetAssistantBlocks(HttpContext http, [FromServices] IBackendConsoleAssetService assets) =>
        ServeAsset(http, assets, AssistantBlocksAsset);

    internal static IResult GetAssistantTransport(HttpContext http, [FromServices] IBackendConsoleAssetService assets) =>
        ServeAsset(http, assets, AssistantTransportAsset);

    internal static IResult GetAssistantStyles(HttpContext http, [FromServices] IBackendConsoleAssetService assets) =>
        ServeAsset(http, assets, AssistantStylesAsset);

    private static IResult ServeAsset(
        HttpContext http,
        IBackendConsoleAssetService assets,
        BackendConsoleAsset asset)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(asset);
    }
}
