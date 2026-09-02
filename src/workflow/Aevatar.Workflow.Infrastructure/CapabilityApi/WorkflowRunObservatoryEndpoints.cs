using Aevatar.Audit;
using Aevatar.Audit.Hosting.EndpointAudit;
using Aevatar.Authentication.Abstractions;
using Aevatar.BackendConsole.Hosting;
using Aevatar.Capabilities;
using Aevatar.Workflow.Application.Abstractions.Observatory;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;

namespace Aevatar.Workflow.Infrastructure.CapabilityApi;

// 06-19-workflow-run-observatory (C2) + 06-20-observatory-admin-cross-scope: read-only run viewer surface.
//   - ALL data endpoints are GET-only + bearer (RequireAuthorization). For a normal caller, scope is implicit =
//     their own scope_id claim, so they can only ever see their own runs; a cross-scope runId -> 404.
//   - A caller with aevatar admin access may pass
//     `scope=<id>` or `scope=__all__` to view another scope / all scopes (G2 auth matrix). Admin status is never
//     self-asserted by a query param; a non-admin cross-scope request is denied BEFORE any cross-scope query runs.
//   - A caller with aevatar admin access may also use /admin/runs/{runId} when they know only the run id and need
//     the service to resolve the owning scope from the workflow current-state read model.
//   - Endpoint audit metadata marks these read surfaces; the host audit middleware writes sanitized request/outcome
//     artifacts and never stores the bearer (G5).
//   - The read-only guard (GET-only + query-ports-only) and inline-page guard still hold; the NyxID authorizer
//     lives here in the endpoint layer, never in the query service.
public static class WorkflowRunObservatoryEndpoints
{
    private const string PageRoute = "/workflow/observatory";
    private const string CallbackRoute = "/workflow/observatory/callback";
    private const string DataRoutePrefix = "/api/workflow/observatory";

    private static readonly BackendConsoleAsset PageAsset = new(
        LogicalName: "workflow-observatory",
        Assembly: typeof(WorkflowRunObservatoryEndpoints).Assembly,
        ResourceSuffix: "CapabilityApi.workflow-observatory.html",
        ContentType: "text/html",
        InjectHostConfiguration: true);

    // Sentinel scope meaning "all scopes" (admin overview). Not a real scope id.
    internal const string AllScopesToken = "__all__";

    public static IEndpointRouteBuilder MapWorkflowRunObservatory(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGet(PageRoute, GetObservatoryPage)
            .WithTags("WorkflowObservatory")
            .WithName("GetWorkflowObservatoryPage")
            .WithSummary("Read-only workflow run observatory served from an embedded static asset.")
            .AllowAnonymous();

        app.MapGet(CallbackRoute, GetObservatoryPage)
            .WithTags("WorkflowObservatory")
            .WithName("GetWorkflowObservatoryCallback")
            .WithSummary("OIDC PKCE redirect target consumed by the observatory page JS.")
            .AllowAnonymous();

        var data = app.MapGroup(DataRoutePrefix).WithTags("WorkflowObservatory");

        data.MapGet("/me", GetMe)
            .WithName("GetWorkflowObservatoryCaller")
            .WithSummary("Caller identity + whether they have aevatar admin access (drives the admin UI).")
            .WithEndpointAudit(
                "workflow.observatory.get-caller",
                AuditSensitivityLevel.Internal,
                "workflow-observatory-caller",
                EndpointAuditTargetResolvers.Static("workflow-observatory-caller", "me"))
            .RequireAuthorization();

        data.MapGet("/runs", ListRuns)
            .WithName("ListWorkflowObservatoryRuns")
            .WithSummary("List runs. Default = caller scope; admins may pass scope=<id> or scope=__all__.")
            .WithEndpointAudit(
                "workflow.observatory.list-runs",
                AuditSensitivityLevel.Confidential,
                "workflow-observatory-runs",
                ResolveWorkflowObservatoryTarget("workflow-observatory-runs"),
                WorkflowObservatoryRequestSummary)
            .RequireAuthorization();

        data.MapGet("/runs/{runId}", GetRun)
            .WithName("GetWorkflowObservatoryRun")
            .WithSummary("Run timeline + summary + usage. Admins may pass scope=<id> for another scope's run.")
            .WithEndpointAudit(
                "workflow.observatory.get-run",
                AuditSensitivityLevel.Confidential,
                "workflow-run",
                ResolveWorkflowObservatoryTarget("workflow-run"),
                WorkflowObservatoryRequestSummary)
            .RequireAuthorization();

        data.MapGet("/runs/{runId}/graph", GetRunGraph)
            .WithName("GetWorkflowObservatoryRunGraph")
            .WithSummary("Run topology. Admins may pass scope=<id> for another scope's run.")
            .WithEndpointAudit(
                "workflow.observatory.get-run-graph",
                AuditSensitivityLevel.Confidential,
                "workflow-run",
                ResolveWorkflowObservatoryTarget("workflow-run"),
                WorkflowObservatoryRequestSummary)
            .RequireAuthorization();

        data.MapGet("/admin/runs/{runId}", GetAdminRun)
            .WithName("GetWorkflowObservatoryAdminRun")
            .WithSummary("Admin-only run timeline + summary + usage, resolved by run id across all scopes.")
            .WithEndpointAudit(
                "workflow.observatory.admin.get-run",
                AuditSensitivityLevel.Confidential,
                "workflow-run",
                ResolveWorkflowObservatoryTarget("workflow-run"),
                WorkflowObservatoryRequestSummary)
            .RequireAuthorization();

        data.MapGet("/admin/runs/{runId}/graph", GetAdminRunGraph)
            .WithName("GetWorkflowObservatoryAdminRunGraph")
            .WithSummary("Admin-only run topology, resolved by run id across all scopes.")
            .WithEndpointAudit(
                "workflow.observatory.admin.get-run-graph",
                AuditSensitivityLevel.Confidential,
                "workflow-run",
                ResolveWorkflowObservatoryTarget("workflow-run"),
                WorkflowObservatoryRequestSummary)
            .RequireAuthorization();

        data.MapGet("/resolve-scope", ResolveScope)
            .WithName("ResolveWorkflowObservatoryScope")
            .WithSummary("Admin-only: resolve a NyxID email to candidate scope id(s).")
            .WithEndpointAudit(
                "workflow.observatory.resolve-scope",
                AuditSensitivityLevel.Restricted,
                "workflow-observatory-scope-resolution",
                EndpointAuditTargetResolvers.Static("workflow-observatory-scope-resolution", "email-lookup"),
                WorkflowObservatoryRequestSummary)
            .RequireAuthorization();

        return app;
    }

    internal static IResult GetObservatoryPage(
        HttpContext http,
        [FromServices] IBackendConsoleAssetService assets)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(assets);
        return assets.Serve(PageAsset);
    }

    internal static async Task<IResult> GetMe(
        HttpContext http,
        [FromServices] IPlatformAdminAuthorizer authorizer,
        CancellationToken ct = default)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var scopeId))
            return Results.Unauthorized();

        var caller = TryGetBearer(http, out var token)
            ? await authorizer.ResolveCallerAsync(token, ct)
            : PlatformCaller.NotElevated;

        return Results.Json(new
        {
            isAdmin = caller.IsElevated,
            role = caller.Role,
            email = caller.Email,
            grantSource = caller.GrantSource,
            scopeId,
        });
    }

    internal static async Task<IResult> ListRuns(
        HttpContext http,
        [FromServices] IWorkflowRunObservatoryQueryService observatory,
        [FromServices] IWorkflowRunAdminQueryService adminQuery,
        [FromServices] IPlatformAdminAuthorizer authorizer,
        [FromServices] ILoggerFactory loggerFactory,
        string? scope = null,
        string? status = null,
        string? origin = null,
        string? definition = null,
        string? schedule = null,
        string? from = null,
        string? to = null,
        int take = 100,
        CancellationToken ct = default)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var ownScopeId))
            return Results.Unauthorized();

        var filter = new ObservatoryRunListFilter
        {
            Status = status,
            Origins = SplitCsv(origin),
            DefinitionActorIds = SplitCsv(definition),
            ScheduleIds = SplitCsv(schedule),
            FromUtc = ParseTimestamp(from),
            ToUtc = ParseTimestamp(to),
            Take = take,
        };

        // No cross-scope intent (or explicitly the caller's own scope) -> unchanged own-scope path, no NyxID call.
        if (!IsCrossScope(scope, ownScopeId))
            return Results.Json(await observatory.ListRunsForScopeAsync(ownScopeId, filter, ct));

        var (denied, _, _) = await AuthorizeCrossScopeAsync(
            http, ownScopeId, scope!, runId: null, action: "list", authorizer, loggerFactory, ct);
        if (denied is not null)
            return denied;

        var runs = string.Equals(scope, AllScopesToken, StringComparison.Ordinal)
            ? await adminQuery.ListAllRunsAsync(filter, ct)
            : await observatory.ListRunsForScopeAsync(scope!, filter, ct);
        return Results.Json(runs);
    }

    internal static async Task<IResult> GetRun(
        HttpContext http,
        string runId,
        [FromServices] IWorkflowRunObservatoryQueryService observatory,
        [FromServices] IPlatformAdminAuthorizer authorizer,
        [FromServices] ILoggerFactory loggerFactory,
        string? scope = null,
        CancellationToken ct = default)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var ownScopeId))
            return Results.Unauthorized();

        var targetScope = ownScopeId;
        if (IsCrossScope(scope, ownScopeId))
        {
            var (denied, _, _) = await AuthorizeCrossScopeAsync(
                http, ownScopeId, scope!, runId, action: "detail", authorizer, loggerFactory, ct);
            if (denied is not null)
                return denied;
            targetScope = scope!;
        }

        var detail = await observatory.GetRunForScopeAsync(targetScope, runId, ct);
        return detail == null ? Results.NotFound() : Results.Json(detail);
    }

    internal static async Task<IResult> GetRunGraph(
        HttpContext http,
        string runId,
        [FromServices] IWorkflowRunObservatoryQueryService observatory,
        [FromServices] IPlatformAdminAuthorizer authorizer,
        [FromServices] ILoggerFactory loggerFactory,
        string? scope = null,
        CancellationToken ct = default)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var ownScopeId))
            return Results.Unauthorized();

        var targetScope = ownScopeId;
        if (IsCrossScope(scope, ownScopeId))
        {
            var (denied, _, _) = await AuthorizeCrossScopeAsync(
                http, ownScopeId, scope!, runId, action: "graph", authorizer, loggerFactory, ct);
            if (denied is not null)
                return denied;
            targetScope = scope!;
        }

        var graph = await observatory.GetRunGraphForScopeAsync(targetScope, runId, ct);
        return graph == null ? Results.NotFound() : Results.Json(graph);
    }

    internal static async Task<IResult> GetAdminRun(
        HttpContext http,
        string runId,
        [FromServices] IWorkflowRunAdminQueryService adminQuery,
        [FromServices] IPlatformAdminAuthorizer authorizer,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var denied = await AuthorizeAdminReadAsync(
            http, runId, action: "admin-detail", authorizer, loggerFactory, ct);
        if (denied is not null)
            return denied;

        var detail = await adminQuery.GetRunAsync(runId, ct);
        return detail == null ? Results.NotFound() : Results.Json(detail);
    }

    internal static async Task<IResult> GetAdminRunGraph(
        HttpContext http,
        string runId,
        [FromServices] IWorkflowRunAdminQueryService adminQuery,
        [FromServices] IPlatformAdminAuthorizer authorizer,
        [FromServices] ILoggerFactory loggerFactory,
        CancellationToken ct = default)
    {
        var denied = await AuthorizeAdminReadAsync(
            http, runId, action: "admin-graph", authorizer, loggerFactory, ct);
        if (denied is not null)
            return denied;

        var graph = await adminQuery.GetRunGraphAsync(runId, ct);
        return graph == null ? Results.NotFound() : Results.Json(graph);
    }

    internal static async Task<IResult> ResolveScope(
        HttpContext http,
        [FromServices] IPlatformAdminAuthorizer authorizer,
        [FromServices] IPlatformUserDirectory directory,
        [FromServices] ILoggerFactory loggerFactory,
        string? email = null,
        CancellationToken ct = default)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var ownScopeId))
            return Results.Unauthorized();

        var (denied, _, token) = await AuthorizeCrossScopeAsync(
            http, ownScopeId, targetScope: "(email-lookup)", runId: null, action: "resolve-scope", authorizer, loggerFactory, ct);
        if (denied is not null)
            return denied;

        if (string.IsNullOrWhiteSpace(email))
            return Results.Json(new { candidates = Array.Empty<object>() });

        var matches = await directory.SearchByEmailAsync(token, email, ct);
        return Results.Json(new
        {
            candidates = matches.Select(match => new { scopeId = match.ScopeId, email = match.Email, role = match.Role }),
        });
    }

    // Cross-scope intent = a non-empty scope that is not the caller's own.
    private static bool IsCrossScope(string? scope, string ownScopeId) =>
        !string.IsNullOrWhiteSpace(scope) && !string.Equals(scope, ownScopeId, StringComparison.Ordinal);

    // Comma-separated multi-value filter param (origin / definition) -> canonical list.
    private static IReadOnlyList<string> SplitCsv(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // ISO-8601 timestamp filter param (from / to) -> DateTimeOffset, or null when absent/unparseable.
    private static DateTimeOffset? ParseTimestamp(string? value) =>
        DateTimeOffset.TryParse(value, out var parsed) ? parsed : null;

    // The single cross-scope authorization gate (G2). Fails closed: missing bearer -> 401; non-elevated -> 403.
    // The cross-scope query is reached only after this returns no denial.
    private static async Task<(IResult? Denied, PlatformCaller Caller, string Token)> AuthorizeCrossScopeAsync(
        HttpContext http,
        string ownScopeId,
        string targetScope,
        string? runId,
        string action,
        IPlatformAdminAuthorizer authorizer,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!TryGetBearer(http, out var token))
        {
            return (Results.Unauthorized(), PlatformCaller.NotElevated, string.Empty);
        }

        var caller = await authorizer.ResolveCallerAsync(token, ct);
        if (!caller.IsElevated)
        {
            return (DeniedResult(), caller, token);
        }

        return (null, caller, token);
    }

    private static async Task<IResult?> AuthorizeAdminReadAsync(
        HttpContext http,
        string runId,
        string action,
        IPlatformAdminAuthorizer authorizer,
        ILoggerFactory loggerFactory,
        CancellationToken ct)
    {
        if (!AevatarScopeAccessGuard.TryGetCallerScopeId(http, out var ownScopeId))
            return Results.Unauthorized();

        var (denied, _, _) = await AuthorizeCrossScopeAsync(
            http,
            ownScopeId,
            targetScope: AllScopesToken,
            runId,
            action,
            authorizer,
            loggerFactory,
            ct);
        return denied;
    }

    private static IResult DeniedResult() =>
        Results.Json(
            new { code = "SCOPE_ACCESS_DENIED", message = "Aevatar admin access required for cross-scope viewing." },
            statusCode: StatusCodes.Status403Forbidden);

    private static bool TryGetBearer(HttpContext http, out string token)
    {
        token = string.Empty;
        var header = http.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var value = header[prefix.Length..].Trim();
        if (value.Length == 0)
            return false;

        token = value;
        return true;
    }

    private static EndpointAuditTargetResolver ResolveWorkflowObservatoryTarget(string targetKind)
    {
        return http =>
        {
            var targetScope = ResolveSafeScopeQuery(http);
            var runId = EndpointAuditSanitizers.SanitizeValue(http.Request.RouteValues["runId"]?.ToString());
            var id = string.IsNullOrWhiteSpace(runId)
                ? targetScope
                : string.IsNullOrWhiteSpace(targetScope)
                    ? runId
                    : $"{targetScope}/{runId}";
            return ValueTask.FromResult<EndpointAuditTarget?>(new EndpointAuditTarget(targetKind, id));
        };
    }

    private static ValueTask<string> WorkflowObservatoryRequestSummary(EndpointAuditSanitizationContext context)
    {
        var parts = new List<string>
        {
            $"{context.HttpContext.Request.Method} {EndpointAuditSanitizers.ResolveRoutePattern(context.HttpContext)}",
        };

        var scope = ResolveSafeScopeQuery(context.HttpContext);
        if (!string.IsNullOrWhiteSpace(scope))
        {
            parts.Add($"scope={scope}");
        }

        var runId = EndpointAuditSanitizers.SanitizeValue(
            context.HttpContext.Request.RouteValues["runId"]?.ToString());
        if (!string.IsNullOrWhiteSpace(runId))
        {
            parts.Add($"runId={runId}");
        }

        return ValueTask.FromResult(string.Join(' ', parts));
    }

    private static string ResolveSafeScopeQuery(HttpContext http)
    {
        return EndpointAuditSanitizers.SanitizeValue(http.Request.Query["scope"].ToString());
    }
}
