using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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
    private const string CallbackRoute = "/workflow/studio/callback";

    public static IEndpointRouteBuilder MapWorkflowStudio(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetStudioPage)
            .WithTags("WorkflowStudio")
            .WithName("GetWorkflowStudioPage")
            .WithSummary("Conversational workflow studio (inline self-contained page).")
            .AllowAnonymous();

        app.MapGet(CallbackRoute, GetStudioPage)
            .WithTags("WorkflowStudio")
            .WithName("GetWorkflowStudioCallback")
            .WithSummary("OIDC PKCE redirect target consumed by the studio page JS.")
            .AllowAnonymous();

        return app;
    }

    internal static IResult GetStudioPage(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Results.Text(WorkflowStudioPage.Html, "text/html", Encoding.UTF8);
    }
}
