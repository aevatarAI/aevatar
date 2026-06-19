using System.Text;
using Aevatar.Capabilities;
using Aevatar.Workflow.Application.Abstractions.Observatory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

// 06-19-workflow-run-observatory (C2): read-only, scope-gated run viewer endpoint surface.
//   - ALL data endpoints are GET-only + bearer (RequireAuthorization). Scope is implicit = the caller's
//     own scope_id claim (no scopeId in the path), so a caller can only ever see their own runs.
//   - Cross-scope runId -> 404 (D8 — the query service returns null; we do not disclose existence).
//   - The page + OIDC callback are AllowAnonymous static shells; all data flows through the bearer APIs.
//   - The service depends on query ports only (no IActorDispatchPort) — this is a pure read surface; the
//     read-only guard (GET-only + query-ports-only) and inline-page (no-wwwroot) guard enforce that.
public static class WorkflowRunObservatoryEndpoints
{
    private const string PageRoute = "/workflow/observatory";
    private const string CallbackRoute = "/workflow/observatory/callback";
    private const string DataRoutePrefix = "/api/workflow/observatory";

    public static IEndpointRouteBuilder MapWorkflowRunObservatory(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetObservatoryPage)
            .WithTags("WorkflowObservatory")
            .WithName("GetWorkflowObservatoryPage")
            .WithSummary("Read-only workflow run observatory (inline self-contained page).")
            .AllowAnonymous();

        app.MapGet(CallbackRoute, GetObservatoryPage)
            .WithTags("WorkflowObservatory")
            .WithName("GetWorkflowObservatoryCallback")
            .WithSummary("OIDC PKCE redirect target consumed by the observatory page JS.")
            .AllowAnonymous();

        var data = app.MapGroup(DataRoutePrefix).WithTags("WorkflowObservatory");

        data.MapGet("/runs", ListRuns)
            .WithName("ListWorkflowObservatoryRuns")
            .WithSummary("List the caller's workflow runs (scope implicit = caller scope_id claim).")
            .RequireAuthorization();

        data.MapGet("/runs/{runId}", GetRun)
            .WithName("GetWorkflowObservatoryRun")
            .WithSummary("Ownership-checked AGUI-shaped run timeline + summary + usage totals.")
            .RequireAuthorization();

        data.MapGet("/runs/{runId}/graph", GetRunGraph)
            .WithName("GetWorkflowObservatoryRunGraph")
            .WithSummary("Ownership-checked run topology (graph-export reuse).")
            .RequireAuthorization();

        return app;
    }

    internal static IResult GetObservatoryPage(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return Results.Text(WorkflowRunObservatoryPage.Html, "text/html", Encoding.UTF8);
    }

    internal static async Task<IResult> ListRuns(
        HttpContext http,
        IWorkflowRunObservatoryQueryService observatory,
        string? status = null,
        int take = 100,
        CancellationToken ct = default)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        var runs = await observatory.ListRunsForScopeAsync(
            scopeId,
            new ObservatoryRunListFilter
            {
                Status = status,
                Take = take,
            },
            ct);
        return Results.Json(runs);
    }

    internal static async Task<IResult> GetRun(
        HttpContext http,
        string runId,
        IWorkflowRunObservatoryQueryService observatory,
        CancellationToken ct = default)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        var detail = await observatory.GetRunForScopeAsync(scopeId, runId, ct);
        return detail == null ? Results.NotFound() : Results.Json(detail);
    }

    internal static async Task<IResult> GetRunGraph(
        HttpContext http,
        string runId,
        IWorkflowRunObservatoryQueryService observatory,
        CancellationToken ct = default)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        var graph = await observatory.GetRunGraphForScopeAsync(scopeId, runId, ct);
        return graph == null ? Results.NotFound() : Results.Json(graph);
    }
}
